using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DungeonRunners.Engine;

namespace DungeonRunners.Data
{
    public sealed class NativeDescriptorCatalog
    {
        private static NativeDescriptorCatalog _instance;
        public static NativeDescriptorCatalog Instance => _instance ??= new NativeDescriptorCatalog();

        private static readonly Regex DungeonZoneName = new Regex(@"^dungeon\d{2}_level", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public readonly List<NativeDescriptorRecord> Records = new List<NativeDescriptorRecord>();
        public readonly List<NativeDescriptorRecord> EncounterTables = new List<NativeDescriptorRecord>();
        public readonly List<NativeDescriptorRecord> ZoneWorldDocs = new List<NativeDescriptorRecord>();
        public readonly List<NativeDescriptorRecord> StaticObjects = new List<NativeDescriptorRecord>();

        private bool _built;
        private string _source = "unknown";

        public bool BuildFromNativeSidecar(NativeSidecarCatalog sidecar)
        {
            Records.Clear();
            EncounterTables.Clear();
            ZoneWorldDocs.Clear();
            StaticObjects.Clear();
            _built = false;

            if (sidecar == null || !sidecar.IsLoaded)
                return false;

            foreach (var entry in sidecar.ZoneWorldEntries)
                AddSidecarRecord(entry, NativeDescriptorKind.ZoneWorld);
            foreach (var entry in sidecar.EncounterTableEntries)
                AddSidecarRecord(entry, NativeDescriptorKind.EncounterTable);
            foreach (var entry in sidecar.StaticObjectEntries)
                AddSidecarRecord(entry, NativeDescriptorKind.StaticObject);

            if (Records.Count == 0 && sidecar.Entries.Count > 0)
            {
                foreach (var entry in sidecar.Entries)
                    AddSidecarRecord(entry, ClassifySidecarEntry(entry));
            }

            _built = true;
            _source = "NativeSidecar";
            Debug.LogError($"[NATIVE-DESCRIPTOR-CATALOG] records={Records.Count} packageEntries={sidecar.Entries.Count}/{sidecar.ManifestEntryCount} packageIndexComplete={sidecar.HasCompletePackageEntryIndex} zoneWorld={ZoneWorldDocs.Count}/239 encounterTables={EncounterTables.Count} staticObjects={StaticObjects.Count} source=NativeSidecar runtimePkgDependency=false native=GCClassRegistry+DFCClass");
            Debug.LogError($"[NATIVE-SIDECAR-SELFTEST] status=LOADED packageEntries={sidecar.Entries.Count}/{sidecar.ManifestEntryCount} packageIndexComplete={sidecar.HasCompletePackageEntryIndex} zoneWorld={sidecar.ZoneWorldEntries.Count} encounterTables={sidecar.EncounterTableEntries.Count} staticObjects={sidecar.StaticObjectEntries.Count} monsterSkills={sidecar.MonsterSkills.Count} effectiveMonsterSkills={sidecar.EffectiveMonsterSkills.Count} fallback=0 source=NativeSidecar");
            return true;
        }

        private void AddSidecarRecord(NativeSidecarEntry entry, NativeDescriptorKind kind)
        {
            var record = new NativeDescriptorRecord
            {
                AuthoredName = entry.Name ?? "",
                RegistryPath = entry.Name ?? "",
                ExtendsPath = "",
                Kind = kind
            };
            record.Properties["entryId"] = entry.EntryId.ToString();
            record.Properties["typeCode"] = entry.TypeCode.ToString();
            record.Properties["rawSha1"] = entry.RawSha1 ?? "";
            record.Properties["decodedSha1"] = entry.DecodedSha1 ?? "";
            record.Properties["textSha1"] = entry.TextSha1 ?? "";
            Records.Add(record);
            if (record.Kind == NativeDescriptorKind.EncounterTable)
                EncounterTables.Add(record);
            else if (record.Kind == NativeDescriptorKind.ZoneWorld)
                ZoneWorldDocs.Add(record);
            else if (record.Kind == NativeDescriptorKind.StaticObject)
                StaticObjects.Add(record);
        }

        public void BuildFromGCDatabase(GCDatabase db)
        {
            Records.Clear();
            EncounterTables.Clear();
            ZoneWorldDocs.Clear();
            StaticObjects.Clear();
            _built = false;

            if (db == null || !db.IsLoaded)
                return;

            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in db.RegisteredPaths)
            {
                GCNode node = db.Resolve(path);
                if (node == null)
                    continue;
                string source = string.IsNullOrWhiteSpace(node.SourceFile) ? path : node.SourceFile;
                if (!seenSources.Add(source))
                    continue;

                var record = CreateRecord(path, node);
                Records.Add(record);
                if (record.Kind == NativeDescriptorKind.EncounterTable)
                    EncounterTables.Add(record);
                else if (record.Kind == NativeDescriptorKind.ZoneWorld)
                    ZoneWorldDocs.Add(record);
                else if (record.Kind == NativeDescriptorKind.StaticObject)
                    StaticObjects.Add(record);
            }

            _built = true;
            _source = "GCDatabase";
            Debug.LogError($"[NATIVE-DESCRIPTOR-CATALOG] records={Records.Count} zoneWorld={ZoneWorldDocs.Count}/239 encounterTables={EncounterTables.Count} staticObjects={StaticObjects.Count} source=GCDatabase expected=PKG-Round4 missingZoneWorld={Math.Max(0, 239 - ZoneWorldDocs.Count)} native=GCClassRegistry+DFCClass");
            bool dewValleyBossZonePresent = ContainsZone("dungeon00_level03_boss");
            bool level03MasterPresent = ContainsRecord("level03_master_encounter");
            bool level04MasterPresent = ContainsRecord("level04_master_encounter");
            Debug.LogError($"[NATIVE-DESCRIPTOR-CATALOG] dewValleyBossZonePresent={dewValleyBossZonePresent} level03MasterPresent={level03MasterPresent} level04MasterPresent={level04MasterPresent} allDungeonReplacement=PARTIAL pendingSpawnUnit=BLOCKED encounterDifficultyConsumer=BLOCKED");
        }

        public void RunStartupSelfTest()
        {
            if (!_built)
                BuildFromGCDatabase(GCDatabase.Instance);

            int wetLike = Records.Count(r => r.HasProperty("WorldEntityTable") || r.HasProperty("WorldEntityGenerator") || r.HasProperty("TableSelector"));
            int lootLike = Records.Count(r => r.AuthoredName.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0 || r.HasProperty("TreasureGenerator") || r.HasProperty("TreasureGenerator2"));
            int monsterLike = Records.Count(r => r.HasProperty("Behavior") || r.HasProperty("CreatureType") || r.HasProperty("CollisionRadius"));
            Debug.LogError($"[NATIVE-DESCRIPTOR-CATALOG-SELFTEST] records={Records.Count} zoneWorld={ZoneWorldDocs.Count}/239 encounterTables={EncounterTables.Count} wetLike={wetLike} lootLike={lootLike} monsterLike={monsterLike} status=PARTIAL source={_source} pkgExpectedZoneWorld=239 pkgMissingZoneWorld={Math.Max(0, 239 - ZoneWorldDocs.Count)}");
        }

        public bool ContainsZone(string name)
        {
            return ZoneWorldDocs.Any(r => string.Equals(r.AuthoredName, name, StringComparison.OrdinalIgnoreCase));
        }

        public bool ContainsRecord(string name)
        {
            return Records.Any(r => string.Equals(r.AuthoredName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static NativeDescriptorRecord CreateRecord(string path, GCNode node)
        {
            var record = new NativeDescriptorRecord
            {
                AuthoredName = string.IsNullOrWhiteSpace(node.SourceFile) ? path : node.SourceFile,
                RegistryPath = path,
                ExtendsPath = node.Extends ?? "",
                Kind = Classify(path, node)
            };

            foreach (var kvp in node.Properties)
                record.Properties[kvp.Key] = kvp.Value;
            CollectProperties(node, record);
            return record;
        }

        private static void CollectProperties(GCNode node, NativeDescriptorRecord record)
        {
            if (node == null)
                return;
            foreach (var child in node.Children.Values)
            {
                foreach (var kvp in child.Properties)
                    AddProperty(record, $"{child.Name}.{kvp.Key}", kvp.Value);
                CollectProperties(child, record);
            }
            for (int i = 0; i < node.AnonymousChildren.Count; i++)
            {
                GCNode child = node.AnonymousChildren[i];
                foreach (var kvp in child.Properties)
                    AddProperty(record, $"*.{i}.{kvp.Key}", kvp.Value);
                CollectProperties(child, record);
            }
        }

        private static NativeDescriptorKind Classify(string path, GCNode node)
        {
            string name = string.IsNullOrWhiteSpace(node.SourceFile) ? path : node.SourceFile;
            string extends = node.Extends ?? "";
            if (DungeonZoneName.IsMatch(name))
                return NativeDescriptorKind.ZoneWorld;
            if (extends.IndexOf("EncounterTable", StringComparison.OrdinalIgnoreCase) >= 0)
                return NativeDescriptorKind.EncounterTable;
            if (extends.IndexOf("StaticObject", StringComparison.OrdinalIgnoreCase) >= 0 || node.GetChild("Description")?.HasProperty("CollisionObject") == true)
                return NativeDescriptorKind.StaticObject;
            if (node.HasProperty("Behavior") || node.GetChild("Description")?.HasProperty("CollisionRadius") == true)
                return NativeDescriptorKind.Monster;
            if (name.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0)
                return NativeDescriptorKind.Loot;
            return NativeDescriptorKind.Other;
        }

        private static NativeDescriptorKind ClassifySidecarEntry(NativeSidecarEntry entry)
        {
            string name = entry?.Name ?? "";
            if (DungeonZoneName.IsMatch(name) || entry?.TypeCode == 15 || entry?.TypeCode == 16)
                return NativeDescriptorKind.ZoneWorld;
            if (entry?.TypeCode == 4)
                return NativeDescriptorKind.StaticObject;
            if (name.IndexOf("enc", StringComparison.OrdinalIgnoreCase) >= 0 &&
                name.IndexOf("world", StringComparison.OrdinalIgnoreCase) >= 0 &&
                name.IndexOf("dungeon", StringComparison.OrdinalIgnoreCase) >= 0)
                return NativeDescriptorKind.EncounterTable;
            if (name.IndexOf("loot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("treasure", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("itemgenerator", StringComparison.OrdinalIgnoreCase) >= 0)
                return NativeDescriptorKind.Loot;
            if (name.IndexOf("creatures", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("mob", StringComparison.OrdinalIgnoreCase) >= 0)
                return NativeDescriptorKind.Monster;
            return NativeDescriptorKind.Other;
        }

        private static void AddProperty(NativeDescriptorRecord record, string key, string value)
        {
            if (!record.Properties.ContainsKey(key))
                record.Properties[key] = value;
        }
    }

    public sealed class NativeDescriptorRecord
    {
        public string AuthoredName;
        public string RegistryPath;
        public string ExtendsPath;
        public NativeDescriptorKind Kind;
        public readonly Dictionary<string, string> Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool HasProperty(string name)
        {
            return Properties.ContainsKey(name) || Properties.Keys.Any(k => k.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public enum NativeDescriptorKind
    {
        Other,
        ZoneWorld,
        EncounterTable,
        Monster,
        StaticObject,
        Loot
    }
}
