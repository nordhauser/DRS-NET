using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mono.Data.Sqlite;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    /// <summary>
    /// Comprehensive item stat database. Parses GC files to resolve the EXACT mods
    /// each mythic item has, then calculates stat values at any player level.
    /// 
    /// Resolution chain:
    ///   Item.ModN -> ModPAL.Quality.ModN -> [N]EnhancementsPAL.AttrName -> AttributesPAL -> Pool + Attribute
    /// 
    /// Tables in dungeon_runners.db:
    ///   stat_pools           - pool formulas (base + level scaling)
    ///   item_resolved_mods   - per-item resolved attributes with pool + value_mult
    /// </summary>
    public class ItemStatDatabase
    {
        private static ItemStatDatabase _instance;
        public static ItemStatDatabase Instance => _instance ??= new ItemStatDatabase();

        public bool IsLoaded { get; private set; }

        // Path B kill switch. Flip to false to revert all non-mythic write sites to the legacy
        // single-ScaleMod cstring behaviour. Mythic path doesn't read this flag.
        public static bool PathBEnabled = true;

        // ═══════════════════════════════════════════════════════════════
        // DATA STRUCTURES
        // ═══════════════════════════════════════════════════════════════

        struct PoolFormula
        {
            public (int Level, float Value)[] Points;
            public float Eval(int level)
            {
                if (Points == null || Points.Length == 0) return 0f;
                if (level <= Points[0].Level) return Points[0].Value;
                var last = Points[Points.Length - 1];
                if (level >= last.Level) return last.Value;
                for (int i = 0; i < Points.Length - 1; i++)
                {
                    var a = Points[i];
                    var b = Points[i + 1];
                    if (level >= a.Level && level <= b.Level)
                        return a.Value + (float)(level - a.Level) / (b.Level - a.Level) * (b.Value - a.Value);
                }
                return last.Value;
            }
        }
        struct ResolvedMod
        {
            public int ModSlot;
            public string Attribute;  // e.g., "MAX_HIT_POINTS"
            public string Pool;       // e.g., "MaxPointBonusPool"
            public float ValueMult;   // 1.0, 0.5, 0.33, 0.25
        }

        // Runtime lookup tables (loaded from DB)
        private Dictionary<string, PoolFormula> _pools = new();
        private Dictionary<string, List<ResolvedMod>> _itemMods = new(StringComparer.OrdinalIgnoreCase);
        // Per-slot mod refs for IG-stub mythics — used by wire serialization (Piece B)
        private Dictionary<string, List<(int Slot, string ModRef)>> _itemWireMods = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _itemReadDataSlotCounts = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _itemReadDataSlotMisses = new(StringComparer.OrdinalIgnoreCase);
        // GCDictionary: set of class names registered in the client (one per .gc class). The
        // dict's sequential numeric IDs are NOT the runtime registry's keys — the client looks up
        // classes by DJB2 hash of the lowercased name (case 0x04 in readType). The dict's value is
        // therefore which prefix form ("items.modpal.X" vs "X") the client registered each class
        // under, so the server hashes the matching form.
        private HashSet<string> _gcClassNames = new(StringComparer.OrdinalIgnoreCase);

        // Parsing intermediaries (used only during population, then cleared)
        private Dictionary<string, (string Attr, string Pool)> _attrMap = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _modPalRefs = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _weaponMythicRefs = new(StringComparer.OrdinalIgnoreCase);
        // IG-stub items: PAL path (lowercased, e.g. "2haxemythicpal.2haxemythic101") → [(slot, generator path)]
        // For direct-Item IG entries (Mythic, Rare, Unique). Wrapper entries (Magic/Superior) go through _wrapperIGEntries.
        private Dictionary<string, List<(int Slot, string GeneratorPath)>> _igStubItems = new(StringComparer.OrdinalIgnoreCase);
        // Per-rarity item counts captured during parse (for boot log breakdown).
        private Dictionary<string, int> _igStubCountByRarity = new(StringComparer.OrdinalIgnoreCase);
        // Wrapper IG entries: rarity ∈ {Magic, Superior} where the inner block uses
        // ItemGenerator = items.ig.X.NormalYIG (no direct Item=). Resolved in a second pass by
        // recursively reading the target IG's direct Item= entries and storing wire mods under
        // composite key "palpath:rarity".
        private List<(string Rarity, string TargetIGRef, List<(int Slot, string GeneratorPath)> Generators)> _wrapperIGEntries = new();
        // Mod generator tables: "{MGFile}.{Section}" → ordered list of ItemModifier refs
        private Dictionary<string, List<string>> _modGenerators = new(StringComparer.OrdinalIgnoreCase);
        // Mod generator inheritance: "{MGFile}.{Section}" → "{ParentFile}.{ParentSection}" (sections with empty body inherit)
        private Dictionary<string, string> _modGeneratorParents = new(StringComparer.OrdinalIgnoreCase);

        private string _gcDir;

        private struct GcTextSource
        {
            public string FileName;
            public string Stem;
            public string Text;
            public string SourcePath;
        }

        private IEnumerable<GcTextSource> EnumerateGcSources(string searchPattern)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(_gcDir))
            {
                foreach (var file in Directory.GetFiles(_gcDir, searchPattern))
                {
                    string fileName = Path.GetFileName(file);
                    seen.Add(fileName);
                    yield return new GcTextSource
                    {
                        FileName = fileName,
                        Stem = Path.GetFileNameWithoutExtension(file),
                        Text = File.ReadAllText(file),
                        SourcePath = file
                    };
                }
            }

            var packageCatalog = NativePackageCatalog.Instance;
            if (!packageCatalog.IsLoaded)
                packageCatalog.LoadFromAssets();
            if (!packageCatalog.IsLoaded)
                yield break;

            foreach (var doc in packageCatalog.EnumerateGcTextDocuments(searchPattern))
            {
                if (doc == null || string.IsNullOrWhiteSpace(doc.FileName) || seen.Contains(doc.FileName))
                    continue;
                seen.Add(doc.FileName);
                yield return new GcTextSource
                {
                    FileName = doc.FileName,
                    Stem = doc.Stem,
                    Text = doc.Text,
                    SourcePath = doc.Name
                };
            }
        }

        private bool TryReadGcSource(string fileName, out GcTextSource source)
        {
            source = default;
            string path = Path.Combine(_gcDir, fileName);
            if (File.Exists(path))
            {
                source = new GcTextSource
                {
                    FileName = Path.GetFileName(path),
                    Stem = Path.GetFileNameWithoutExtension(path),
                    Text = File.ReadAllText(path),
                    SourcePath = path
                };
                return true;
            }

            var packageCatalog = NativePackageCatalog.Instance;
            if (!packageCatalog.IsLoaded)
                packageCatalog.LoadFromAssets();
            if (packageCatalog.TryGetGcText(fileName, out var doc))
            {
                source = new GcTextSource
                {
                    FileName = doc.FileName,
                    Stem = doc.Stem,
                    Text = doc.Text,
                    SourcePath = doc.Name
                };
                return true;
            }
            return false;
        }

        // ═══════════════════════════════════════════════════════════════
        // LOAD
        // ═══════════════════════════════════════════════════════════════

        public void Load()
        {
            try
            {
                _gcDir = DungeonRunners.Core.DataPaths.GcDir;
                bool hasBundledGcDir = Directory.Exists(_gcDir);
                if (!NativePackageCatalog.Instance.IsLoaded)
                    NativePackageCatalog.Instance.LoadFromAssets();
                if (!hasBundledGcDir && !NativePackageCatalog.Instance.IsLoaded)
                {
                    Debug.LogError($"[ItemStatDB] GC directory not found and NativePackageCatalog unavailable: {_gcDir}");
                    return;
                }

                using var conn = Database.GameDatabase.GetConnection();
                CreateTables(conn);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM item_resolved_mods";
                    long count = (long)cmd.ExecuteScalar();

                    // Detect needs-repopulate: empty, OR no IG-stub entries (pre-Phase-5 DB),
                    // OR no Path B non-mythic wire mods (pre-Path-B DB).
                    bool needsRepopulate = count == 0;
                    if (!needsRepopulate)
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM item_resolved_mods WHERE full_gc_key='2haxemythicpal.2haxemythic101'";
                        long stubProbe = (long)cmd.ExecuteScalar();
                        needsRepopulate = stubProbe == 0;
                    }
                    if (!needsRepopulate)
                    {
                        // Path B canary v3: weapon IGs use `ItemTimeLine` (capital L) while
                        // mage/plate IGs use `ItemTimeline` (lowercase l). The first cut had
                        // a case-sensitive regex that missed all weapon wrappers. The presence
                        // of weapon wrapper rows confirms the case-insensitive parser ran.
                        cmd.CommandText = "SELECT COUNT(*) FROM item_wire_mods WHERE full_gc_key LIKE '2hcrossbow%:rare' LIMIT 1";
                        long weaponProbe = (long)cmd.ExecuteScalar();
                        needsRepopulate = weaponProbe == 0;
                    }
                    if (!needsRepopulate)
                    {
                        // Stale-prefix sentinel: if ANY wire-mod row still has the old
                        // items.pal. prefix, the parser ran pre-fix → force rebuild.
                        cmd.CommandText = "SELECT COUNT(*) FROM item_wire_mods WHERE full_gc_key LIKE 'items.pal.%' LIMIT 1";
                        long staleProbe = (long)cmd.ExecuteScalar();
                        needsRepopulate = staleProbe > 0;
                    }
                    if (!needsRepopulate)
                    {
                        // Phase 5c stat canary: wrapper IG rows must have resolved stats as
                        // well as wire-mod children so equipment recompute matches the same
                        // authored ItemModifier chain the client displays.
                        cmd.CommandText = "SELECT COUNT(*) FROM item_resolved_mods WHERE full_gc_key='magebodypal.normal001:magic'";
                        long wrapperStatProbe = (long)cmd.ExecuteScalar();
                        needsRepopulate = wrapperStatProbe == 0;
                    }

                    if (needsRepopulate)
                    {
                        Debug.LogError($"[ItemStatDB] Populating from GC files (existing rows={count}, full rebuild)...");
                        using (var del = conn.CreateCommand())
                        {
                            del.CommandText = "DELETE FROM item_resolved_mods; DELETE FROM item_wire_mods;";
                            del.ExecuteNonQuery();
                        }
                        PopulateFromGCFiles(conn);
                    }
                    else
                    {
                        Debug.LogError($"[ItemStatDB] Already populated: {count} mod entries");
                    }
                }

                LoadPools(conn);
                LoadResolvedMods(conn);
                LoadWireMods(conn);
                LoadGCDictionary();

                IsLoaded = true;

                Debug.LogError($"[ItemStatDB] Loaded: {_pools.Count} pools, {_itemMods.Count} items, {_itemMods.Values.Sum(v => v.Count)} total mods, {_itemWireMods.Count} wire-mod items, {_gcClassNames.Count} GC class names");

                // Path B sanity probes — surfaces parser regressions at boot.
                foreach (var probeKey in new[] {
                    "2haxemythicpal.2haxemythic101",           // existing mythic (Diabolical) - regression canary
                    "magebodypal.rare001",                      // Tier 1 direct-Item Rare
                    "magebodypal.unique001",                    // Tier 1 direct-Item Unique (Mage)
                    "platepal.plateuniquearmor1",               // Tier 1 direct-Item Unique (Plate)
                    "magebodypal.normal001:magic",              // Tier 2 wrapper Magic
                    "magebodypal.normal001:superior"            // Tier 2 wrapper Superior
                })
                {
                    if (_itemWireMods.TryGetValue(probeKey, out var probe))
                        Debug.LogError($"[ItemStatDB] PROBE {probeKey}: {probe.Count} wire mods -> [{string.Join(", ", probe.Select(p => p.ModRef))}]");
                    else
                        Debug.LogError($"[ItemStatDB] PROBE {probeKey}: MISSING");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemStatDB] Load failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // POPULATION FROM GC FILES
        // ═══════════════════════════════════════════════════════════════

        private void PopulateFromGCFiles(SqliteConnection conn)
        {
            ParseAttributesPAL();
            Debug.LogError($"[ItemStatDB] Phase 1: {_attrMap.Count} attribute mappings");

            ParseAllModPALs();
            Debug.LogError($"[ItemStatDB] Phase 2: {_modPalRefs.Count} ModPAL references");

            ParseWeaponMythicModPAL();
            Debug.LogError($"[ItemStatDB] Phase 3: {_weaponMythicRefs.Count} weapon mythic references");

            int itemCount = 0, modCount = 0;
            using var transaction = conn.BeginTransaction();

            InsertPools(conn);

            // Parse named mythic items (*MythicPAL.gc, excluding *ModPAL.gc)
            foreach (var source in EnumerateGcSources("*MythicPAL.gc"))
            {
                string fileName = source.Stem;
                if (fileName.Contains("Mod")) continue;

                var items = ParseItemFile(source.Text, fileName, false);
                foreach (var (itemName, mods) in items)
                {
                    string fullKey = $"{fileName}.{itemName}".ToLowerInvariant();
                    var resolved = ResolveMods(mods);
                    if (resolved.Count > 0)
                    {
                        InsertResolvedMods(conn, fullKey, resolved);
                        modCount += resolved.Count;
                        itemCount++;
                    }
                }
            }

            // Parse prebuilt items in *PAL.gc files
            foreach (var source in EnumerateGcSources("*PAL.gc"))
            {
                string fileName = source.Stem;
                if (fileName.Contains("Mod") || fileName.Contains("Enhancement") ||
                    fileName.Contains("Attribute") || fileName.Contains("Mythic") ||
                    fileName.Contains("Pool") || fileName.Contains("Weapon") && fileName.EndsWith("PAL")) continue;
                // More precise: only process files that actually contain MythicPreBuilt
                string peek = source.Text;
                if (!peek.Contains("MythicPreBuilt")) continue;

                var items = ParseItemFile(source.Text, fileName, true);
                foreach (var (itemName, mods) in items)
                {
                    string fullKey = $"{fileName}.{itemName}".ToLowerInvariant();
                    var resolved = ResolveMods(mods);
                    if (resolved.Count > 0)
                    {
                        InsertResolvedMods(conn, fullKey, resolved);
                        modCount += resolved.Count;
                        itemCount++;
                    }
                }
            }

            Debug.LogError($"[ItemStatDB] Phase 4: {itemCount} items, {modCount} resolved mod attributes stored");

            // Phase 5: IG-stub items across all rarities (Mythic/Rare/Unique direct-Item IGs +
            // Magic/Superior wrapper IGs that delegate to NormalYIG). Wire mods get injected at write
            // sites; client renders 2-7 visible bonuses per native data per tier.
            // Chain: {Rarity}*IG.gc Item=X + ItemModGeneratorN=Y → *MG.gc section Y → ModPAL.Quality.ModN refs.
            ParseAllIGFiles();
            string rarityBreakdown = string.Join(" ", _igStubCountByRarity.OrderBy(k => k.Key).Select(k => $"{k.Key.ToLowerInvariant()}={k.Value}"));
            Debug.LogError($"[ItemStatDB] Phase 5a: {_igStubItems.Count} direct-Item IG entries ({rarityBreakdown}) + {_wrapperIGEntries.Count} wrapper-IG entries");

            ParseAllMGFiles();
            Debug.LogError($"[ItemStatDB] Phase 5b: {_modGenerators.Count} mod generators parsed");

            int igItemCount = 0, igStatCount = 0, igWireCount = 0;
            foreach (var kvp in _igStubItems)
            {
                string itemPalPath = kvp.Key;
                var generators = kvp.Value;

                // For each slot, pick the FIRST mod ref deterministically.
                var perSlotModRefs = new List<(int Slot, string ModRef)>();
                foreach (var (slot, generatorPath) in generators)
                {
                    string normGen = generatorPath;
                    if (normGen.StartsWith("items.mg.", StringComparison.OrdinalIgnoreCase))
                        normGen = normGen.Substring("items.mg.".Length);
                    if (_modGenerators.TryGetValue(normGen, out var modRefs) && modRefs.Count > 0)
                    {
                        perSlotModRefs.Add((slot, modRefs[0]));
                    }
                }
                if (perSlotModRefs.Count == 0) continue;

                // Stat resolution (existing ResolveMods handles ModPAL.Quality.ModN chain).
                var statResolved = ResolveMods(perSlotModRefs.Select(p => (p.Slot, p.ModRef)).ToList());
                if (statResolved.Count > 0)
                {
                    InsertResolvedMods(conn, itemPalPath, statResolved);
                    igStatCount += statResolved.Count;
                }

                // Wire serialization (all slots, including Binder which has no stats).
                InsertWireMods(conn, itemPalPath, perSlotModRefs);
                igWireCount += perSlotModRefs.Count;
                igItemCount++;
            }

            // Phase 5c — wrapper IG resolution (Magic/Superior). For each wrapper entry, parse the
            // target IG (e.g. NormalMageBodyIG.gc) for its direct Item= rows, and store the
            // wrapper's mod generators under composite key "palpath:rarity".
            int wrapItemCount = 0, wrapStatCount = 0, wrapWireCount = 0;
            foreach (var entry in _wrapperIGEntries)
            {
                string targetIGName = entry.TargetIGRef.Split('.').Last();
                if (!TryReadGcSource(targetIGName + ".gc", out var targetSource)) continue;

                var targetItems = ParseDirectItemEntries(targetSource.Text);
                if (targetItems.Count == 0) continue;

                var perSlotModRefs = new List<(int Slot, string ModRef)>();
                foreach (var (slot, generatorPath) in entry.Generators)
                {
                    string normGen = generatorPath;
                    if (normGen.StartsWith("items.mg.", StringComparison.OrdinalIgnoreCase))
                        normGen = normGen.Substring("items.mg.".Length);
                    if (_modGenerators.TryGetValue(normGen, out var modRefs) && modRefs.Count > 0)
                        perSlotModRefs.Add((slot, modRefs[0]));
                }
                if (perSlotModRefs.Count == 0) continue;

                var statResolved = ResolveMods(perSlotModRefs.Select(p => (p.Slot, p.ModRef)).ToList());
                foreach (var targetPalPath in targetItems)
                {
                    string compositeKey = $"{targetPalPath}:{entry.Rarity}".ToLowerInvariant();
                    if (statResolved.Count > 0)
                    {
                        InsertResolvedMods(conn, compositeKey, statResolved);
                        wrapStatCount += statResolved.Count;
                    }
                    InsertWireMods(conn, compositeKey, perSlotModRefs);
                    wrapWireCount += perSlotModRefs.Count;
                    wrapItemCount++;
                }
            }

            transaction.Commit();
            Debug.LogError($"[ItemStatDB] Phase 5: {igItemCount} direct-Item items, {igStatCount} stat mods, {igWireCount} wire mods stored; Phase 5c: {wrapItemCount} wrapper:rarity rows, {wrapStatCount} stat mods, {wrapWireCount} wire mods stored");

            // Clear parsing intermediaries after authored IG/MG rows have been materialized.
            _attrMap.Clear();
            _modPalRefs.Clear();
            _weaponMythicRefs.Clear();
            _igStubItems.Clear();
            _igStubCountByRarity.Clear();
            _wrapperIGEntries.Clear();
            _modGenerators.Clear();
            _modGeneratorParents.Clear();
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 1: Parse AttributesPAL
        // ═══════════════════════════════════════════════════════════════

        private void ParseAttributesPAL()
        {
            if (!TryReadGcSource("AttributesPAL.gc", out var source)) { Debug.LogError("[ItemStatDB] AttributesPAL.gc not found!"); return; }

            string content = source.Text.Replace("\r", "");
            string currentName = null, currentPool = null;

            foreach (string line in content.Split('\n'))
            {
                string t = line.Trim();
                var m = Regex.Match(t, @"^(\w+)\s+extends\s+PoolTables\.(\w+)");
                if (m.Success) { currentName = m.Groups[1].Value; currentPool = m.Groups[2].Value; continue; }
                var m2 = Regex.Match(t, @"Attribute\s*=\s*(\w+);");
                if (m2.Success && currentName != null) { _attrMap[currentName] = (m2.Groups[1].Value, currentPool); currentName = null; }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 2: Parse all ModPAL files
        // ═══════════════════════════════════════════════════════════════

        private void ParseAllModPALs()
        {
            foreach (var source in EnumerateGcSources("*ModPAL.gc"))
            {
                string fileName = source.Stem;
                if (fileName == "WeaponMythicModPAL") continue;
                ParseSingleModPAL(source.Text, fileName);
            }
        }

        private void ParseSingleModPAL(string sourceText, string fileName)
        {
            string content = sourceText.Replace("\r", "");
            string currentQuality = null;
            string pendingSection = null;

            foreach (string line in content.Split('\n'))
            {
                string t = line.Trim();

                // Detect quality section names (bare word on its own line, { comes next line)
                // e.g., "Superior" or "Rare" or "Magic" or "Unique"
                if (!t.Contains("extends") && !t.Contains("=") && !t.Contains("//") && !t.StartsWith("*"))
                {
                    // "{" alone on a line? Confirm previous pending section
                    if (t == "{" && pendingSection != null)
                    {
                        currentQuality = pendingSection;
                        pendingSection = null;
                        continue;
                    }

                    // "SectionName {" on same line
                    if (t.EndsWith("{") && !t.Contains("Description"))
                    {
                        string section = t.TrimEnd('{', ' ', '\t');
                        if (section.Length > 0 && section.Length < 30)
                            currentQuality = section;
                        continue;
                    }

                    // Bare word — might be a section name, save as pending
                    var sectionMatch = Regex.Match(t, @"^(\w+)$");
                    if (sectionMatch.Success && t.Length < 30 && !t.Contains("Description"))
                        pendingSection = t;
                }

                // Match mod lines
                var m = Regex.Match(t, @"^(Mod\d+)\s+extends\s+(\S+)");
                if (m.Success && currentQuality != null)
                {
                    string key = $"{fileName}|{currentQuality}|{m.Groups[1].Value}";
                    _modPalRefs[key] = m.Groups[2].Value.TrimEnd('{', ' ');
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 3: Parse WeaponMythicModPAL
        // ═══════════════════════════════════════════════════════════════

        private void ParseWeaponMythicModPAL()
        {
            if (!TryReadGcSource("WeaponMythicModPAL.gc", out var source)) return;

            string content = source.Text.Replace("\r", "");
            foreach (string line in content.Split('\n'))
            {
                string t = line.Trim();
                var m = Regex.Match(t, @"^(\w+)\s+extends\s+(\d*EnhancementsPAL\.\w+)");
                if (m.Success)
                    _weaponMythicRefs[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 4: Parse Item Files
        // ═══════════════════════════════════════════════════════════════

        private List<(string Name, List<(int Slot, string Ref)>)> ParseItemFile(string sourceText, string fileName, bool prebuiltOnly)
        {
            var result = new List<(string, List<(int, string)>)>();
            string content = sourceText.Replace("\r", "");
            string[] lines = content.Split('\n');

            string currentItem = null;
            var currentMods = new List<(int, string)>();

            foreach (string rawLine in lines)
            {
                string t = rawLine.Trim();
                if (t.StartsWith("//") || t.StartsWith("/*")) continue;

                // Check for item/prebuilt start: "SomeName extends SomeParent"
                var itemMatch = Regex.Match(t, @"^(\w+)\s+extends\s+\S+");
                if (itemMatch.Success)
                {
                    string name = itemMatch.Groups[1].Value;
                    bool isPrebuilt = name.StartsWith("MythicPreBuilt", StringComparison.OrdinalIgnoreCase);
                    bool isNamedMythic = !isPrebuilt && !name.StartsWith("Mod") && !name.StartsWith("Description") &&
                        !name.StartsWith("One") && !name.StartsWith("Two") && !name.StartsWith("Three") &&
                        !name.StartsWith("Four") && !name.StartsWith("Five") && !name.StartsWith("static") &&
                        !name.StartsWith("Base") && name != fileName;

                    bool shouldParse = prebuiltOnly ? isPrebuilt : isNamedMythic;
                    if (shouldParse)
                    {
                        // Save previous item
                        if (currentItem != null && currentMods.Count > 0)
                            result.Add((currentItem, new List<(int, string)>(currentMods)));
                        currentItem = name;
                        currentMods.Clear();
                    }
                }

                // Collect Mod lines for current item
                if (currentItem != null)
                {
                    var modMatch = Regex.Match(t, @"^Mod(\d+)\s+extends\s+(\S+)");
                    if (modMatch.Success)
                    {
                        int slot = int.Parse(modMatch.Groups[1].Value);
                        string extendsRef = modMatch.Groups[2].Value.TrimEnd('{', ' ');
                        if (!extendsRef.Contains("ItemModifier"))
                            currentMods.Add((slot, extendsRef));
                    }
                }
            }

            // Save last item
            if (currentItem != null && currentMods.Count > 0)
                result.Add((currentItem, currentMods));

            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5a: Parse {Rare,Unique,Magic,Superior,Mythic}*IG.gc files
        // ═══════════════════════════════════════════════════════════════

        // Loops the rarity-prefix whitelist instead of a single glob — excludes MerchantSpecialEventIG,
        // BlingGnomeIG, NormalIG, etc. which use LinkedGenerator references and don't carry
        // item-block-level Item= / ItemModGeneratorN= entries.
        private void ParseAllIGFiles()
        {
            string[] rarityPrefixes = { "Rare", "Unique", "Magic", "Superior", "Mythic" };
            foreach (var prefix in rarityPrefixes)
            {
                _igStubCountByRarity[prefix] = 0;
                foreach (var source in EnumerateGcSources(prefix + "*IG.gc"))
                    ParseSingleIGFile(source.Text, prefix);
            }
        }

        private void ParseSingleIGFile(string sourceText, string fileRarity)
        {
            string content = sourceText.Replace("\r", "");
            string currentItemPalPath = null;
            string currentItemGenRef = null;
            var currentGenerators = new List<(int, string)>();

            void Flush()
            {
                if (currentGenerators.Count > 0)
                {
                    if (currentItemPalPath != null)
                    {
                        _igStubItems[currentItemPalPath] = new List<(int, string)>(currentGenerators);
                        _igStubCountByRarity[fileRarity]++;
                    }
                    else if (currentItemGenRef != null)
                    {
                        // Wrapper IG (Magic/Superior pattern: ItemGenerator = items.ig.X.NormalYIG)
                        _wrapperIGEntries.Add((fileRarity, currentItemGenRef, new List<(int, string)>(currentGenerators)));
                    }
                }
                currentItemPalPath = null;
                currentItemGenRef = null;
                currentGenerators.Clear();
            }

            foreach (string rawLine in content.Split('\n'))
            {
                string t = rawLine.Trim();
                if (t.StartsWith("//") || t.StartsWith("/*")) continue;

                // Inner-block header: matches direct-Item blocks (Rare/Unique/Mythic via ItemTimeline.*)
                // and wrapper blocks (Magic/Superior via RandomItemGenerator). SingleItemGenerator
                // included for NormalAmuletIG-style files (no mod generators -> harmlessly skipped).
                // The outer container `extends ItemGeneratorTable` is excluded by this list.
                // Case-insensitive: weapon IGs (2HCrossbow, 1HPick etc.) use "ItemTimeLine" with
                // capital L while mage/plate IGs use "ItemTimeline" lowercase. Both are valid in
                // the native data.
                if (Regex.IsMatch(t, @"^\w+\s+extends\s+(ItemTimeline\.\w+|RandomItemGenerator|SingleItemGenerator)", RegexOptions.IgnoreCase))
                {
                    Flush();
                    continue;
                }

                var itemMatch = Regex.Match(t, @"^Item\s*=\s*([^;\s]+)");
                if (itemMatch.Success)
                {
                    // Normalize storage key: strip items.pal. prefix so it matches what
                    // NormalizeGCClass produces at lookup. Mythic IG files use bare paths
                    // (e.g. "2HAxeMythicPAL.2HAxeMythic1"); mage IG files use the prefixed
                    // form ("items.pal.MageBodyPAL.Rare001"). Without normalization,
                    // prefixed entries never hit at lookup.
                    string rawItem = itemMatch.Groups[1].Value.ToLowerInvariant();
                    if (rawItem.StartsWith("items.pal."))
                        rawItem = rawItem.Substring("items.pal.".Length);
                    currentItemPalPath = rawItem;
                    continue;
                }

                // Wrapper IG marker: "ItemGenerator = items.ig.X.NormalYIG"
                var wrapperMatch = Regex.Match(t, @"^ItemGenerator\s*=\s*([^;\s]+)");
                if (wrapperMatch.Success)
                {
                    currentItemGenRef = wrapperMatch.Groups[1].Value;
                    continue;
                }

                var genMatch = Regex.Match(t, @"^ItemModGenerator(\d+)\s*=\s*([^;\s]+)");
                if (genMatch.Success)
                {
                    int slot = int.Parse(genMatch.Groups[1].Value);
                    currentGenerators.Add((slot, genMatch.Groups[2].Value));
                }
            }

            // Final flush
            Flush();
        }

        // Reads a single IG file and returns lowercased palpaths from direct Item= rows. Used by
        // wrapper IG resolution to enumerate the target IG's items (e.g. NormalMageBodyIG → all
        // MageBodyPAL.Normal### entries).
        private List<string> ParseDirectItemEntries(string sourceText)
        {
            var result = new List<string>();
            string content = sourceText.Replace("\r", "");
            foreach (string rawLine in content.Split('\n'))
            {
                string t = rawLine.Trim();
                if (t.StartsWith("//") || t.StartsWith("/*")) continue;
                var m = Regex.Match(t, @"^Item\s*=\s*([^;\s]+)");
                if (m.Success)
                {
                    string raw = m.Groups[1].Value.ToLowerInvariant();
                    if (raw.StartsWith("items.pal."))
                        raw = raw.Substring("items.pal.".Length);
                    result.Add(raw);
                }
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // PHASE 5b: Parse *MG.gc files (mod generator tables)
        // ═══════════════════════════════════════════════════════════════

        private void ParseAllMGFiles()
        {
            foreach (var source in EnumerateGcSources("*MG.gc"))
            {
                string fileName = source.Stem;
                if (fileName.EndsWith("IG", StringComparison.OrdinalIgnoreCase)) continue; // safety

                string content = source.Text.Replace("\r", "");
                string currentSection = null;
                var currentMods = new List<string>();

                foreach (string rawLine in content.Split('\n'))
                {
                    string t = rawLine.Trim();
                    if (t.StartsWith("//") || t.StartsWith("/*")) continue;

                    // Save previous before starting new section
                    void FlushPrevious()
                    {
                        if (currentSection != null)
                            _modGenerators[$"{fileName}.{currentSection}"] = new List<string>(currentMods);
                    }

                    // Form 1: "XXX extends ItemModifierGeneratorTable" — section with its own mods
                    var sectionMatch = Regex.Match(t, @"^(\w+)\s+extends\s+ItemModifierGeneratorTable");
                    if (sectionMatch.Success)
                    {
                        FlushPrevious();
                        currentSection = sectionMatch.Groups[1].Value;
                        currentMods.Clear();
                        continue;
                    }

                    // Form 2: "XXX extends items.mg.YYY.ZZZ" — section that inherits from another MG section
                    var inheritMatch = Regex.Match(t, @"^(\w+)\s+extends\s+items\.mg\.(\w+)\.(\w+)");
                    if (inheritMatch.Success)
                    {
                        FlushPrevious();
                        currentSection = inheritMatch.Groups[1].Value;
                        currentMods.Clear();
                        _modGeneratorParents[$"{fileName}.{currentSection}"] = $"{inheritMatch.Groups[2].Value}.{inheritMatch.Groups[3].Value}";
                        continue;
                    }

                    var modMatch = Regex.Match(t, @"^ItemModifier\s*=\s*([^;\s]+)");
                    if (modMatch.Success && currentSection != null)
                        currentMods.Add(modMatch.Groups[1].Value);
                }

                // Final flush — record the section even if mods are empty (so inheritance still applies)
                if (currentSection != null)
                    _modGenerators[$"{fileName}.{currentSection}"] = new List<string>(currentMods);
            }

            ResolveModGeneratorInheritance();
        }

        // Walk inheritance chains so sections that inherit (empty body) get their parent's mods.
        private void ResolveModGeneratorInheritance()
        {
            foreach (var key in _modGeneratorParents.Keys.ToList())
            {
                // Skip if this section already has its own mods
                if (_modGenerators.TryGetValue(key, out var own) && own.Count > 0) continue;

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
                string parent = _modGeneratorParents[key];
                while (parent != null && !visited.Contains(parent))
                {
                    visited.Add(parent);
                    if (_modGenerators.TryGetValue(parent, out var parentMods) && parentMods.Count > 0)
                    {
                        _modGenerators[key] = new List<string>(parentMods);
                        break;
                    }
                    _modGeneratorParents.TryGetValue(parent, out parent);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // MOD RESOLUTION
        // ═══════════════════════════════════════════════════════════════

        private List<ResolvedMod> ResolveMods(List<(int Slot, string Ref)> mods)
        {
            var result = new List<ResolvedMod>();

            foreach (var (slot, rawRef) in mods)
            {
                string modRef = rawRef.Replace("items.modpal.", "").Replace("items.pal.", "");
                string enhancementRef = ResolveToEnhancement(modRef);
                if (enhancementRef == null) continue;

                // Parse: "[N]EnhancementsPAL.SomeName"
                var enhMatch = Regex.Match(enhancementRef, @"(\d*)EnhancementsPAL\.(\w+)");
                if (!enhMatch.Success) continue;

                int enhCount = string.IsNullOrEmpty(enhMatch.Groups[1].Value) ? 1 : int.Parse(enhMatch.Groups[1].Value);
                string enhName = enhMatch.Groups[2].Value;
                float valueMult = enhCount > 0 ? 1.0f / enhCount : 1.0f;

                // Round to avoid float issues: 0.33 for 3, 0.25 for 4, 0.5 for 2
                if (enhCount == 3) valueMult = 0.33f;
                else if (enhCount == 4) valueMult = 0.25f;
                else if (enhCount == 2) valueMult = 0.5f;

                var attrNames = SplitEnhancementName(enhName, enhCount);
                foreach (string attrName in attrNames)
                {
                    if (_attrMap.TryGetValue(attrName, out var mapping))
                    {
                        result.Add(new ResolvedMod
                        {
                            ModSlot = slot,
                            Attribute = mapping.Attr,
                            Pool = mapping.Pool,
                            ValueMult = valueMult
                        });
                    }
                    else
                    {
                        Debug.LogWarning($"[ItemStatDB] Unknown attribute: {attrName} from {enhName}");
                    }
                }
            }

            return result;
        }

        private string ResolveToEnhancement(string modRef)
        {
            if (modRef.Contains("EnhancementsPAL")) return modRef;
            if (modRef.Contains("ItemModifier") || modRef.Contains("ProcModPAL")) return null;

            // WeaponMythicModPAL.SomeName
            if (modRef.StartsWith("WeaponMythicModPAL.", StringComparison.OrdinalIgnoreCase))
            {
                string name = modRef.Substring("WeaponMythicModPAL.".Length);
                return _weaponMythicRefs.TryGetValue(name, out string r) ? r : null;
            }

            // ModPAL.Quality.ModN (e.g., "AxeModPAL.Rare.Mod2")
            string[] parts = modRef.Split('.');
            if (parts.Length >= 3)
            {
                string key = $"{parts[0]}|{parts[1]}|{parts[2]}";
                if (_modPalRefs.TryGetValue(key, out string enhRef))
                    return enhRef.Contains("EnhancementsPAL") ? enhRef : ResolveToEnhancement(enhRef);
            }

            return null;
        }

        private List<string> SplitEnhancementName(string name, int expectedCount)
        {
            var result = new List<string>();
            if (expectedCount <= 1) { result.Add(name); return result; }

            // Split on '_' boundaries between known attribute names
            // Strategy: greedily match known attribute names from left to right
            string remaining = name;
            while (remaining.Length > 0 && result.Count < expectedCount)
            {
                bool found = false;
                // Try longest possible prefix that matches a known attribute
                for (int len = remaining.Length; len > 0; len--)
                {
                    string candidate = remaining.Substring(0, len);
                    if (_attrMap.ContainsKey(candidate))
                    {
                        result.Add(candidate);
                        remaining = remaining.Substring(len);
                        if (remaining.StartsWith("_")) remaining = remaining.Substring(1);
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    // Try splitting at next underscore
                    int idx = remaining.IndexOf('_');
                    if (idx > 0)
                    {
                        string part = remaining.Substring(0, idx);
                        if (_attrMap.ContainsKey(part)) result.Add(part);
                        remaining = remaining.Substring(idx + 1);
                    }
                    else
                    {
                        if (_attrMap.ContainsKey(remaining)) result.Add(remaining);
                        break;
                    }
                }
            }

            return result;
        }

        // ═══════════════════════════════════════════════════════════════
        // DATABASE OPERATIONS
        // ═══════════════════════════════════════════════════════════════

        private void CreateTables(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS stat_pools (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    pool_name TEXT NOT NULL UNIQUE,
                    base_value REAL NOT NULL, scale REAL NOT NULL, divisor REAL NOT NULL);
                CREATE TABLE IF NOT EXISTS item_resolved_mods (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    full_gc_key TEXT NOT NULL,
                    mod_slot INTEGER NOT NULL,
                    attribute TEXT NOT NULL,
                    pool_name TEXT NOT NULL,
                    value_mult REAL NOT NULL,
                    UNIQUE(full_gc_key, mod_slot, attribute));
                CREATE INDEX IF NOT EXISTS idx_item_mods_key ON item_resolved_mods(full_gc_key);
                CREATE TABLE IF NOT EXISTS item_wire_mods (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    full_gc_key TEXT NOT NULL,
                    mod_slot INTEGER NOT NULL,
                    mod_ref TEXT NOT NULL,
                    UNIQUE(full_gc_key, mod_slot));
                CREATE INDEX IF NOT EXISTS idx_item_wire_mods_key ON item_wire_mods(full_gc_key);";
            cmd.ExecuteNonQuery();
        }

        private void InsertPools(SqliteConnection conn)
        {
            var pools = new (string N, float B, float S, float D)[] {
                ("MaxPointBonusPool",           50f,  7950f, 109f),
                ("PrimaryAttributeBonusPool",   20f,  1090f, 109f),
                ("DamageBonusPool",             10f,  3270f, 109f),
                ("DamageModPool",                5f,   545f, 109f),
                ("AttackRatingBonusPool",       10f,  1090f, 109f),
                ("AttackDefenseRatingModPool",   5f,   545f, 109f),
                ("DefenseRatingBonusPool",      10f,  1090f, 109f),
                ("DamageResistBonusPool",        5f,   545f, 109f),
                ("SpeedModPool",                 2f,   218f, 109f),
                ("SizeModPool",                  1f,   109f, 109f),
                ("BlockModPool",                 5f,   545f, 109f),
                ("CriticalHitModPool",           3f,   327f, 109f),
                ("StealBonusPool",               2f,   218f, 109f),
                ("RegenModPool",                 5f,   545f, 109f),
                ("StunBonusPool",                3f,   327f, 109f),
                ("DamageReflectBonusPool",       2f,   218f, 109f),
            };
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO stat_pools (pool_name,base_value,scale,divisor) VALUES(@n,@b,@s,@d)";
            var pN = cmd.Parameters.Add("@n", System.Data.DbType.String);
            var pB = cmd.Parameters.Add("@b", System.Data.DbType.Double);
            var pS = cmd.Parameters.Add("@s", System.Data.DbType.Double);
            var pD = cmd.Parameters.Add("@d", System.Data.DbType.Double);
            foreach (var p in pools) { pN.Value = p.N; pB.Value = p.B; pS.Value = p.S; pD.Value = p.D; cmd.ExecuteNonQuery(); }
        }

        private void InsertResolvedMods(SqliteConnection conn, string fullKey, List<ResolvedMod> mods)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO item_resolved_mods (full_gc_key,mod_slot,attribute,pool_name,value_mult) VALUES(@k,@s,@a,@p,@v)";
            var pK = cmd.Parameters.Add("@k", System.Data.DbType.String);
            var pS = cmd.Parameters.Add("@s", System.Data.DbType.Int32);
            var pA = cmd.Parameters.Add("@a", System.Data.DbType.String);
            var pP = cmd.Parameters.Add("@p", System.Data.DbType.String);
            var pV = cmd.Parameters.Add("@v", System.Data.DbType.Double);

            pK.Value = fullKey;
            foreach (var mod in mods)
            {
                pS.Value = mod.ModSlot; pA.Value = mod.Attribute; pP.Value = mod.Pool; pV.Value = mod.ValueMult;
                cmd.ExecuteNonQuery();
            }
        }

        private void InsertWireMods(SqliteConnection conn, string fullKey, List<(int Slot, string ModRef)> wireMods)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO item_wire_mods (full_gc_key,mod_slot,mod_ref) VALUES(@k,@s,@r)";
            var pK = cmd.Parameters.Add("@k", System.Data.DbType.String);
            var pS = cmd.Parameters.Add("@s", System.Data.DbType.Int32);
            var pR = cmd.Parameters.Add("@r", System.Data.DbType.String);
            pK.Value = fullKey;
            foreach (var (slot, modRef) in wireMods)
            {
                pS.Value = slot;
                pR.Value = modRef;
                cmd.ExecuteNonQuery();
            }
        }

        private void LoadPools(SqliteConnection conn)
        {
            _pools = DefaultPoolCurves();
        }

        private static Dictionary<string, PoolFormula> DefaultPoolCurves()
        {
            return new Dictionary<string, PoolFormula>
            {
                ["MaxPointBonusPool"] = new PoolFormula { Points = new[] { (1, 50f), (110, 8000f) } },
                ["PrimaryAttributeBonusPool"] = new PoolFormula { Points = new[] { (1, 20f), (110, 1110f) } },
                ["DamageBonusPool"] = new PoolFormula { Points = new[] { (1, 10f), (110, 882f) } },
                ["DamageModPool"] = new PoolFormula { Points = new[] { (1, 5f), (100, 20f), (125, 25f) } },
                ["AttackRatingBonusPool"] = new PoolFormula { Points = new[] { (1, 32f), (110, 3520f) } },
                ["AttackDefenseRatingModPool"] = new PoolFormula { Points = new[] { (1, 10f), (100, 50f), (105, 55f) } },
                ["DefenseRatingBonusPool"] = new PoolFormula { Points = new[] { (1, 19f), (110, 1550f) } },
                ["DamageResistBonusPool"] = new PoolFormula { Points = new[] { (1, 26f), (110, 2860f) } },
                ["SpeedModPool"] = new PoolFormula { Points = new[] { (1, 10f), (100, 40f), (105, 45f) } },
                ["SizeModPool"] = new PoolFormula { Points = new[] { (1, 20f), (100, 50f), (110, 55f) } },
                ["BlockModPool"] = new PoolFormula { Points = new[] { (1, 3f), (100, 13f), (110, 15f) } },
                ["CriticalHitModPool"] = new PoolFormula { Points = new[] { (1, 100f), (110, 544f) } },
                ["StealBonusPool"] = new PoolFormula { Points = new[] { (1, 10f), (100, 20f), (115, 25f) } },
                ["RegenModPool"] = new PoolFormula { Points = new[] { (1, 10f), (100, 100f), (110, 105f) } },
                ["StunBonusPool"] = new PoolFormula { Points = new[] { (1, 20f), (100, 90f), (120, 95f) } },
                ["DamageReflectBonusPool"] = new PoolFormula { Points = new[] { (1, 50f), (110, 380f) } },
            };
        }

        private void LoadResolvedMods(SqliteConnection conn)
        {
            _itemMods.Clear();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT full_gc_key,mod_slot,attribute,pool_name,value_mult FROM item_resolved_mods";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                if (!_itemMods.TryGetValue(key, out var list)) { list = new List<ResolvedMod>(); _itemMods[key] = list; }
                list.Add(new ResolvedMod { ModSlot = r.GetInt32(1), Attribute = r.GetString(2), Pool = r.GetString(3), ValueMult = r.GetFloat(4) });
            }
        }

        private void LoadWireMods(SqliteConnection conn)
        {
            _itemWireMods.Clear();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT full_gc_key,mod_slot,mod_ref FROM item_wire_mods ORDER BY full_gc_key, mod_slot";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string key = r.GetString(0);
                if (!_itemWireMods.TryGetValue(key, out var list))
                {
                    list = new List<(int, string)>();
                    _itemWireMods[key] = list;
                }
                list.Add((r.GetInt32(1), r.GetString(2)));
            }
        }

        // Loads the authored class catalog at server boot. File format: one entry per line,
        // "<numeric_id> <case-sensitive class name>". We discard the dict's numeric ID (it's a file
        // order index, not the runtime registry's key) and keep the name set so we can pick the
        // correct prefix form ("items.modpal.X" vs "X") for hashing.
        private void LoadGCDictionary()
        {
            _gcClassNames.Clear();
            string path = Path.Combine(_gcDir, "..", "GCDictionary.dict");
            if (!File.Exists(path))
            {
                Debug.LogError($"[ItemStatDB] GCDictionary.dict not found at {path} — Phase 2 IG-stub mod injection will not work");
                return;
            }
            using var sr = new StreamReader(path);
            string line;
            while ((line = sr.ReadLine()) != null)
            {
                int sp = line.IndexOf(' ');
                if (sp <= 0 || sp >= line.Length - 1) continue;
                string name = line.Substring(sp + 1).Trim();
                if (name.Length > 0)
                    _gcClassNames.Add(name);
            }
        }

        // Compute the runtime hash the client uses for a GC class. The client looks up classes by
        // DJB2(lowercased name) in readType case 0x04. The wrinkle: the dict registers classes under
        // varying prefix conventions (AxeCraftedModPAL.X has no prefix; items.modpal.FighterModPal.X
        // does). We probe the dict to find the actual registered form, then hash that.
        public uint GetGCClassHash(string className)
        {
            if (!IsLoaded || string.IsNullOrEmpty(className)) return 0;
            // Try the name as-given (case-insensitive match against dict)
            if (_gcClassNames.Contains(className))
                return ComputeDJB2(className);
            // If name has prefix, try without
            if (className.StartsWith("items.modpal.", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = className.Substring("items.modpal.".Length);
                if (_gcClassNames.Contains(stripped))
                    return ComputeDJB2(stripped);
            }
            else
            {
                string prefixed = "items.modpal." + className;
                if (_gcClassNames.Contains(prefixed))
                    return ComputeDJB2(prefixed);
            }
            return 0;
        }

        private static uint ComputeDJB2(string s)
        {
            uint h = 5381;
            foreach (char c in s.ToLowerInvariant()) h = h * 33 + (uint)c;
            return h;
        }

        public bool TryGetItemReadDataSlotCount(string gcClass, out int slotCount)
        {
            slotCount = 0;
            if (string.IsNullOrWhiteSpace(gcClass))
                return false;

            string key = NormalizeGCClass(gcClass);
            if (_itemReadDataSlotCounts.TryGetValue(key, out slotCount))
                return true;
            if (_itemReadDataSlotMisses.Contains(key))
                return false;

            GCNode node = ResolveItemReadDataNode(key, out string resolvedKey);
            if (node == null)
            {
                _itemReadDataSlotMisses.Add(key);
                return false;
            }

            int childSlots = CountItemReadDataModifierChildren(node);
            slotCount = Math.Max(1, 1 + childSlots);
            _itemReadDataSlotCounts[key] = slotCount;
            if (!string.IsNullOrWhiteSpace(resolvedKey))
                _itemReadDataSlotCounts[resolvedKey] = slotCount;
            return true;
        }

        private GCNode ResolveItemReadDataNode(string key, out string resolvedKey)
        {
            resolvedKey = key;
            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return null;

            foreach (string candidate in GetItemReadDataCandidates(key))
            {
                var node = gc.ResolveWithInheritance(candidate);
                if (node != null)
                {
                    resolvedKey = NormalizeGCClass(candidate);
                    return node;
                }
            }
            return null;
        }

        private IEnumerable<string> GetItemReadDataCandidates(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                yield break;

            string normalized = key.Replace('\\', '.').Replace('/', '.').Trim();
            yield return normalized;

            if (normalized.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
            {
                yield return normalized.Substring("items.pal.".Length);
            }
            else
            {
                yield return "items.pal." + normalized;
            }
        }

        private int CountItemReadDataModifierChildren(GCNode node)
        {
            int count = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string childName in node.ChildOrder)
            {
                if (!seen.Add(childName))
                    continue;
                if (node.Children.TryGetValue(childName, out var child) && IsItemReadDataModifierChild(child))
                    count++;
            }

            foreach (var kvp in node.Children)
            {
                if (!seen.Add(kvp.Key))
                    continue;
                if (IsItemReadDataModifierChild(kvp.Value))
                    count++;
            }

            foreach (var child in node.AnonymousChildren)
            {
                if (IsItemReadDataModifierChild(child))
                    count++;
            }

            return count;
        }

        private bool IsItemReadDataModifierChild(GCNode node)
        {
            if (node == null || string.Equals(node.Name, "Description", StringComparison.OrdinalIgnoreCase))
                return false;
            return ExtendsItemModifier(node.Extends, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        private bool ExtendsItemModifier(string typeName, HashSet<string> visited)
        {
            if (IsItemModifierBase(typeName))
                return true;
            if (string.IsNullOrWhiteSpace(typeName))
                return false;

            string key = typeName.Trim().TrimEnd('{', ';');
            if (!visited.Add(key))
                return false;

            var gc = GCDatabase.Instance;
            if (gc == null || !gc.IsLoaded)
                return false;

            GCNode parent = gc.Resolve(key);
            if (parent == null && key.StartsWith("items.modpal.", StringComparison.OrdinalIgnoreCase))
                parent = gc.Resolve(key.Substring("items.modpal.".Length));
            if (parent == null && key.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                parent = gc.Resolve(key.Substring("items.pal.".Length));
            if (parent == null)
                return false;

            if (IsItemModifierBase(parent.Name) || IsItemModifierBase(parent.Extends))
                return true;
            return ExtendsItemModifier(parent.Extends, visited);
        }

        private static bool IsItemModifierBase(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return false;
            string leaf = typeName.Trim();
            int dot = leaf.LastIndexOf('.');
            if (dot >= 0)
                leaf = leaf.Substring(dot + 1);
            return leaf.Equals("ItemModifier", StringComparison.OrdinalIgnoreCase) ||
                   leaf.Equals("ItemAttributeModifier", StringComparison.OrdinalIgnoreCase);
        }

        // ═══════════════════════════════════════════════════════════════
        // RUNTIME LOOKUP
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Get ALL stat bonuses for a mythic item at a given player level.
        /// slotDivisor: weapons=8, armor varies by slot (use old formulas for armor).
        /// Returns dictionary of attribute -> calculated value.
        /// </summary>
        public Dictionary<string, int> GetItemStats(string gcClass, int playerLevel, int slotDivisor = 8)
        {
            return GetItemStatsAtItemLevel(gcClass, playerLevel + 3, slotDivisor);
        }

        public Dictionary<string, int> GetItemStatsAtItemLevel(string gcClass, int itemLevel, int slotDivisor = 8, int rarity = -1)
        {
            var result = new Dictionary<string, int>();
            if (!IsLoaded || string.IsNullOrEmpty(gcClass)) return result;

            string key = ResolveResolvedModKey(gcClass, rarity);
            if (!_itemMods.TryGetValue(key, out var mods)) return result;

            itemLevel = Math.Max(1, itemLevel);

            foreach (var mod in mods)
            {
                if (!_pools.TryGetValue(mod.Pool, out var pool)) continue;
                float poolValue = pool.Eval(itemLevel);
                int bonus = (int)(poolValue * mod.ValueMult / slotDivisor);

                if (result.ContainsKey(mod.Attribute))
                    result[mod.Attribute] += bonus;
                else
                    result[mod.Attribute] = bonus;
            }

            return result;
        }

        /// <summary>
        /// Get HP-relevant stats: (directHP, endurance, mana).
        /// </summary>
        public (int hp, int endurance, int mana) GetItemHPStats(string gcClass, int playerLevel)
        {
            var stats = GetItemStats(gcClass, playerLevel);
            stats.TryGetValue("MAX_HIT_POINTS", out int hp);
            stats.TryGetValue("ENDURANCE", out int end);
            stats.TryGetValue("MAX_MANA_POINTS", out int mana);
            return (hp, end, mana);
        }

        /// <summary>
        /// Get the per-slot wire-mod refs for an IG-stub mythic item (Billy's Goat, Diabolical, etc).
        /// Returns ordered list of (slot, modRef) — empty if item isn't IG-stub.
        /// Piece B (wire serialization) uses these to inject mod children into equipment packets.
        /// </summary>
        public List<(int Slot, string ModRef)> GetItemWireMods(string gcClass)
        {
            if (!IsLoaded || string.IsNullOrEmpty(gcClass)) return new List<(int, string)>();
            string key = NormalizeGCClass(gcClass);
            if (_itemWireMods.TryGetValue(key, out var list))
                return new List<(int, string)>(list);
            return new List<(int, string)>();
        }

        /// <summary>
        /// Wrapper-IG lookup for items whose mod set depends on the drop's rarity (Magic/Superior).
        /// The same Normal PAL item (e.g. MageBodyPAL.Normal001) is referenced by both
        /// MagicMageBodyIG (3 mod gens) and SuperiorMageBodyIG (2 mod gens). The wire-mods table
        /// stores both under composite keys "palpath:Magic" and "palpath:Superior" — pass the
        /// drop's actual rarity to pick the right one.
        /// </summary>
        public List<(int Slot, string ModRef)> GetWrapperIGWireMods(string gcClass, string rarity)
        {
            if (!IsLoaded || string.IsNullOrEmpty(gcClass) || string.IsNullOrEmpty(rarity)) return new List<(int, string)>();
            string normalized = NormalizeGCClass(gcClass);
            string authoredRarity = NormalizeAuthoredRarityName(rarity);
            string key = string.IsNullOrEmpty(authoredRarity) ? "" : $"{normalized}:{authoredRarity}";
            if (_itemWireMods.TryGetValue(key, out var list))
                return new List<(int, string)>(list);
            return new List<(int, string)>();
        }

        /// <summary>Check if an item has resolved mods in the database.</summary>
        public bool HasItem(string gcClass, int rarity = -1)
        {
            if (!IsLoaded) return false;
            return _itemMods.ContainsKey(ResolveResolvedModKey(gcClass, rarity));
        }

        /// <summary>Get list of attribute names an item has (for logging).</summary>
        public List<string> GetItemAttributes(string gcClass, int rarity = -1)
        {
            string key = ResolveResolvedModKey(gcClass, rarity);
            if (_itemMods.TryGetValue(key, out var mods))
                return mods.Select(m => m.Attribute).Distinct().ToList();
            return new List<string>();
        }

        // ═══════════════════════════════════════════════════════════════
        // GC CLASS NORMALIZATION
        // ═══════════════════════════════════════════════════════════════

        private string NormalizeGCClass(string gcClass)
        {
            string lower = gcClass.ToLowerInvariant();
            if (lower.StartsWith("items.pal."))
                lower = lower.Substring("items.pal.".Length);
            return lower;
        }

        private static string NormalizeAuthoredRarityName(string rarity)
        {
            if (string.IsNullOrWhiteSpace(rarity)) return null;
            string r = rarity.Trim().ToLowerInvariant();
            return r == "magical" ? "magic" : r;
        }

        private static string NormalizeAuthoredRarityName(int rarity)
        {
            return rarity switch
            {
                0 => "normal",
                1 => "superior",
                2 => "magic",
                3 => "rare",
                4 => "unique",
                5 => "mythic",
                _ => null
            };
        }

        private string ResolveResolvedModKey(string gcClass, int rarity)
        {
            string key = NormalizeGCClass(gcClass);
            string authoredRarity = NormalizeAuthoredRarityName(rarity);
            if (!string.IsNullOrEmpty(authoredRarity))
            {
                string compositeKey = $"{key}:{authoredRarity}";
                if (_itemMods.ContainsKey(compositeKey))
                    return compositeKey;
            }
            return key;
        }

        /// <summary>For external logging. Extracts the last part of a GC class.</summary>
        public static string ExtractPattern(string gcClass)
        {
            if (string.IsNullOrEmpty(gcClass)) return "";
            string[] parts = gcClass.Split('.');
            return parts.Length > 0 ? parts[parts.Length - 1] : gcClass;
        }
    }
}
