using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using DungeonRunners.Engine;

namespace DungeonRunners.Core
{
    public static class RuntimeEvidenceManager
    {
        private static readonly object LogLock = new object();
        private static bool _started;
        private static bool _logHooked;
        private static StreamWriter _logWriter;
        private static string _logPath;
        private static DateTime _lastLogFlushUtc = DateTime.MinValue;
        private static int _pendingLogLines;
        private static bool _focusedFilterInstalled;
        private static ILogHandler _originalLogHandler;
        private static Mutex _singleInstanceMutex;
        private static bool _ownsSingleInstanceMutex;
        private static bool _shouldAbortStartup;
        private static readonly Dictionary<string, int> FallbackHitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static bool ShouldAbortStartup => _shouldAbortStartup;

        public static void EnsureStarted()
        {
            if (_started) return;
            _started = true;

            if (!TryAcquireSingleInstanceMutex())
            {
                _shouldAbortStartup = true;
                WriteDuplicateLaunchNotice("mutex", 0);
                Application.quitting += Stop;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return;
            }

            if (IsOtherRuntimeProcessActive(out int otherRuntimePid))
            {
                _shouldAbortStartup = true;
                WriteDuplicateLaunchNotice("process", otherRuntimePid);
                ReleaseSingleInstanceMutex();
                Application.quitting += Stop;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                return;
            }

            if (IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_SELF_EVIDENCE")))
                return;

            if (!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_FOCUSED_LOGS")))
                SetFocusedLogFilter(!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_VERBOSE_EVIDENCE_LOGS")));

            if (!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_SELF_LOGS")))
                StartLogMirror();

            if (!IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_SELF_WIRE_CAPTURE")))
                StartWireCapture();

            Application.quitting += Stop;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public static void Stop()
        {
            lock (LogLock)
            {
                if (_logHooked)
                {
                    Application.logMessageReceivedThreaded -= OnLogMessage;
                    _logHooked = false;
                }

                if (_logWriter != null)
                {
                    _logWriter.Flush();
                    _logWriter.Dispose();
                    _logWriter = null;
                }

                if (_focusedFilterInstalled)
                {
                    DungeonRunners.Engine.Debug.logger.logHandler = _originalLogHandler;
                    _originalLogHandler = null;
                    _focusedFilterInstalled = false;
                }

                ReleaseSingleInstanceMutex();
            }
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            Stop();
        }

        public static void SetFocusedLogFilter(bool enabled)
        {
            lock (LogLock)
            {
                if (enabled)
                {
                    if (_focusedFilterInstalled) return;
                    _originalLogHandler = DungeonRunners.Engine.Debug.logger.logHandler;
                    DungeonRunners.Engine.Debug.logger.logHandler = new FocusedLogHandler(_originalLogHandler);
                    _focusedFilterInstalled = true;
                }
                else
                {
                    if (!_focusedFilterInstalled) return;
                    DungeonRunners.Engine.Debug.logger.logHandler = _originalLogHandler;
                    _originalLogHandler = null;
                    _focusedFilterInstalled = false;
                }
            }
        }

        private static void StartLogMirror()
        {
            try
            {
                string logDir = ResolveClientLogsDir();
                int pid = Process.GetCurrentProcess().Id;
                _logPath = ResolveServerLogPath(logDir);
                string targetDir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(targetDir))
                    Directory.CreateDirectory(targetDir);
                try
                {
                    if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 0)
                    {
                        string prevPath = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(_logPath) + ".prev" + Path.GetExtension(_logPath));
                        File.Copy(_logPath, prevPath, true);
                    }
                }
                catch { }
                _logWriter = new StreamWriter(new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                _logWriter.AutoFlush = false;
                _lastLogFlushUtc = DateTime.UtcNow;
                _pendingLogLines = 0;
                _logHooked = true;
                Application.logMessageReceivedThreaded += OnLogMessage;
                WriteLogLine("[RUNTIME-EVIDENCE] pid=" + pid + " log=" + _logPath);
                DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Mirroring server log to " + _logPath);
                LogBuildBinding("mirror");
            }
            catch (Exception ex)
            {
                DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Server log mirror failed: " + ex.Message);
            }
        }

        public static void LogBuildBinding(string source)
        {
            string marker = string.IsNullOrWhiteSpace(source) ? "[BUILD-BINDING] " : "[BUILD-BINDING] source=" + source + " ";
            DungeonRunners.Engine.Debug.LogError(marker + ResolveBuildBinding());
        }

        public static int LogFallbackHit(string area, string key, string detail = null, int throttleEvery = 64)
        {
            area = NormalizeLogToken(area, "unknown");
            key = NormalizeLogToken(key, "unknown");
            string counterKey = area + "|" + key;
            int count;
            lock (FallbackHitCounts)
            {
                FallbackHitCounts.TryGetValue(counterKey, out count);
                count++;
                FallbackHitCounts[counterKey] = count;
            }

            bool shouldLog = count <= 3 || (throttleEvery > 0 && count % throttleEvery == 0);
            if (shouldLog)
            {
                string suffix = string.IsNullOrWhiteSpace(detail) ? "" : " " + detail.Trim();
                DungeonRunners.Engine.Debug.LogError($"[FALLBACK-HIT] area={area} key={key} count={count}{suffix}");
            }
            return count;
        }

        private static string NormalizeLogToken(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            var sb = new StringBuilder(value.Length);
            foreach (char ch in value.Trim())
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == ':')
                    sb.Append(ch);
                else
                    sb.Append('_');
            }
            return sb.Length > 0 ? sb.ToString() : fallback;
        }

        private static void StartWireCapture()
        {
            try
            {
                string repoRoot = ResolveRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture skipped: repo root was not found");
                    return;
                }

                string scriptPath = Path.Combine(repoRoot, "scripts", "start_packet_capture.ps1");
                if (!File.Exists(scriptPath))
                {
                    DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture skipped: " + scriptPath + " is missing");
                    return;
                }

                int pid = Process.GetCurrentProcess().Id;
                string args = "-NoProfile -ExecutionPolicy Bypass -File " + Quote(scriptPath) + " -Single -Name server-auto -OwnerPid " + pid + " -StopWhenOwnerExits";
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    WorkingDirectory = repoRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture failed: powershell did not start");
                        return;
                    }

                    if (!proc.WaitForExit(1000))
                    {
                        DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture launch requested for DR_Server.exe pid=" + pid + " name=server-auto path=" + Path.Combine(ResolveClientLogsDir(), "server-auto.pcapng"));
                        return;
                    }

                    if (proc.ExitCode != 0)
                    {
                        DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture failed immediately: exit=" + proc.ExitCode);
                        return;
                    }

                    DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture attached to DR_Server.exe pid=" + pid + " name=server-auto");
                }
            }
            catch (Exception ex)
            {
                DungeonRunners.Engine.Debug.LogError("[RUNTIME-EVIDENCE] Packet capture failed: " + ex.Message);
            }
        }

        private static bool TryAcquireSingleInstanceMutex()
        {
            try
            {
                _singleInstanceMutex = new Mutex(false, "DungeonRunnersServerEngineRuntime");
                _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0, false);
                if (!_ownsSingleInstanceMutex)
                {
                    _singleInstanceMutex.Dispose();
                    _singleInstanceMutex = null;
                    return false;
                }
                return true;
            }
            catch (AbandonedMutexException)
            {
                _ownsSingleInstanceMutex = true;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex == null)
                return;
            try
            {
                if (_ownsSingleInstanceMutex)
                    _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
            }
            try
            {
                _singleInstanceMutex.Dispose();
            }
            catch
            {
            }
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

        private static void WriteDuplicateLaunchNotice(string reason, int otherPid)
        {
            string line = DateTime.UtcNow.ToString("o") + " [Error] [RUNTIME-EVIDENCE] Duplicate DR_Server.exe launch aborted pid=" + Process.GetCurrentProcess().Id + " otherPid=" + otherPid + " reason=" + reason;
            try
            {
                string path = ResolveServerLogPath(ResolveClientLogsDir());
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
            }
            DungeonRunners.Engine.Debug.LogError(line);
        }

        private static bool IsOtherRuntimeProcessActive(out int otherPid)
        {
            otherPid = 0;
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    string currentPath = "";
                    try
                    {
                        currentPath = current.MainModule?.FileName ?? "";
                    }
                    catch
                    {
                    }

                    string processName = !string.IsNullOrWhiteSpace(currentPath)
                        ? Path.GetFileNameWithoutExtension(currentPath)
                        : current.ProcessName;

                    foreach (var proc in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            if (proc.Id == current.Id)
                                continue;

                            string otherPath = "";
                            try
                            {
                                otherPath = proc.MainModule?.FileName ?? "";
                            }
                            catch
                            {
                            }

                            if (string.IsNullOrWhiteSpace(currentPath) ||
                                string.Equals(otherPath, currentPath, StringComparison.OrdinalIgnoreCase))
                            {
                                otherPid = proc.Id;
                                return true;
                            }
                        }
                        finally
                        {
                            proc.Dispose();
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (!ShouldMirrorLog(condition, type))
                return;

            string line = DateTime.UtcNow.ToString("o") + " [" + type + "] " + condition;
            if (!string.IsNullOrWhiteSpace(stackTrace) && (type == LogType.Exception || type == LogType.Assert))
                line += Environment.NewLine + stackTrace;
            WriteLogLine(line);
        }

        private static string ResolveBuildBinding()
        {
            try
            {
                string buildDir = ResolveBuildDir();
                string buildInfoPath = Path.Combine(buildDir, "build_info.txt");
                if (File.Exists(buildInfoPath))
                    return File.ReadAllText(buildInfoPath).Replace("\r", " ").Replace("\n", " ").Trim();
                return "Runtime=unity BuildInfoMissing=" + buildInfoPath;
            }
            catch (Exception ex)
            {
                return "Runtime=unity BuildInfoError=" + ex.Message;
            }
        }

        private static string ResolveBuildDir()
        {
            try
            {
                string dataPath = Application.dataPath;
                if (!string.IsNullOrWhiteSpace(dataPath))
                {
                    var dir = Directory.GetParent(dataPath);
                    if (dir != null) return dir.FullName;
                }
            }
            catch
            {
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static void WriteLogLine(string line)
        {
            lock (LogLock)
            {
                if (_logWriter == null) return;
                _logWriter.WriteLine(line);
                _pendingLogLines++;
                DateTime now = DateTime.UtcNow;
                if (_pendingLogLines >= 64 || (now - _lastLogFlushUtc).TotalSeconds >= 1.0)
                {
                    _logWriter.Flush();
                    _pendingLogLines = 0;
                    _lastLogFlushUtc = now;
                }
            }
        }

        private static bool ShouldMirrorLog(string condition, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Assert)
                return true;

            if (IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_VERBOSE_EVIDENCE_LOGS")))
                return true;

            if (string.IsNullOrWhiteSpace(condition))
                return false;

            string line = condition.TrimStart();
            if (IsImportantLog(line) || IsFocusedLog(line))
                return true;

            string[] noisyPrefixes =
            {
                "[UDP]",
                "[NEW-PKT]",
                "[OP",
                "[SIZE-CHECK]",
                "[CUMULATIVE-",
                "[PLAYER-SPAWN-HEX]",
                "[MONSTER-SPAWN-HEX]",
                "[DIAG-0x00]",
                "[PLAYER-STATE]",
                "[PACKET-",
                "[NPC-",
                "[Portal",
                "[SendZoneNPCs]",
                "[SendZonePortals]",
                "[SendZoneCheckpoints]",
                "[Checkpoint]",
                "[QUEST-",
                "[GNOME-",
                "[GCObject]",
                "[CHARLIST",
                "[ACTION-READ]",
                "[COMPONENT]",
                "[COMPONENT-ROUTE]",
                "[INVENTORY]",
                "[GROUP]",
                "[ROOM-RNG]",
                "[SPAWN",
                "[DROP-LOAD]",
                "[PASSIVE-",
                "[ClassPassiveData]",
                "[KNOBS]",
                "[MEMBER]",
                "[Combat] Registered",
                "[ZoneSpawnManager]",
                "[FOLLOW",
                "[MOTD]",
                "[WELCOME]",
                "[WriteItem]",
                "[EQUIP-WRITEINIT]",
                "[UDP-RAW]",
                "[UDP-FOLLOW]",
                "[UDP-SKILLS]",
                "[ENTITY-STREAM]",
                "[ENTITY-CH]",
                "[TICK] Starting",
                "[TICK] Using",
                "════",
                "0000",
                "=== OPERATION",
                "=== PACKET",
                "=== FULL",
                "FULL UNITY SPAWN",
                "✓ Operation"
            };

            foreach (string prefix in noisyPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return false;
        }

        private static bool IsImportantLog(string line)
        {
            return line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("CRITICAL", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Invalid ComponentID", StringComparison.OrdinalIgnoreCase) >= 0
                || line.StartsWith("[RUNTIME-EVIDENCE]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[BUILD-BINDING]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[FALLBACK-HIT]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[AUTHORED-COVERAGE]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[DRLog]", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("=== Dungeon Runners Server", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Server Version:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Max Players:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Auth Server component", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Game Server component", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[CONFIG] Loaded", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[CONFIG] Reloaded", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[CONFIG] DB load error", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("[SERVER]", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFocusedLog(string line)
        {
            string[] allowedPrefixes =
            {
                "[Combat]",
                "[COMBAT",
                "[ATTACK]",
                "[ACTION",
                "[NPC]",
                "[DAMAGE]",
                "[GetNearest]",
                "[MON-DAMAGE]",
                "[MON-DAMAGE-SYNC]",
                "[MON-ATTACK]",
                "[MON-STATE]",
                "[MONSTER-DAMAGE]",
                "[MON-CONTACT]",
                "[MON-HP-TRUTH]",
                "[MON-REGEN]",
                "[MON-MANA-REGEN]",
                "[MON-SKILL]",
                "[MON-SKILL-CD]",
                "[MON-MOVE]",
                "[MON-MOVE-SEND]",
                "[MON-PRE-SUFFIX-COMBAT]",
                "[MON-HP-PRIMER]",
                "[ENCOUNTER-MANIFEST]",
                "[ENCOUNTER-MANIFEST-SELFTEST]",
                "[ENCOUNTER-OBJECT]",
                "[DUNGEON-SNAPSHOT]",
                "[DUNGEON-SPAWN]",
                "[DUNGEON-TRANSFORM]",
                "[DUNGEON-PORTAL]",
                "[PROJECTILE-HIT]",
                "[PRE-SUFFIX-DUE-DRAIN]",
                "[RANGED-PROJECTILE]",
                "[RANGED-PROJECTILE-DUE]",
                "[PROJECTILE-ENTITY]",
                "[PLAYER-HP-TRUTH]",
                "[PLAYER-HP-SUFFIX]",
                "[PLAYER-HP-NATIVE",
                "[PLAYER-DAMAGE]",
                "[PLAYER-REGEN]",
                "[PLAYER-REGEN-COOLDOWN]",
                "[HP-PRESERVE]",
                "[SPAWN-HP-FRESH]",
                "[SPAWN-HP-PRESERVE]",
                "[SPAWN-HP-FULL]",
                "[SPAWN-HP-REGEN]",
                "[SPAWN-PKT]",
                "[SPAWN-XP]",
                "[ZONE-HP-PRESERVE]",
                "[ZONE-HP-FULL]",
                "[ZONE-HP-REGEN]",
                "[ZONE-HP-BOOTSTRAP]",
                "[PLAYERSTATE]",
                "[ALLOC-STATS]",
                "[HP-FINAL]",
                "[PLAYER-HIT-DETAIL]",
                "[SPAWN]",
                "[SPAWN-SYNCH]",
                "[SPAWN-TRACK]",
                "[PACKET-2]",
                "[PACKET-3]",
                "[OP12]",
                "[RNG-AUDIT]",
                "[RNG-COMBAT]",
                "[RNG-LEDGER]",
                "[RNG-SEED]",
                "[RUNTIME-SEED]",
                "[NATIVE-VALIDATION-CUTOFF]",
                "[LAYOUT-SEED]",
                "[UDP-RNG]",
                "[SPELL",
                "[LOCAL-MOVE-ACK]",
                "[SEND-COMPRESSEDA]",
                "[SYNC-SUFFIX",
                "[TAKEDAMAGE]",
                "[MANA]",
                "[MANA-0x52]",
                "[USETARGET-",
                "[RANGED-USE-START]",
                "[PROJECTILE-COLLISION]",
                "[WEAPON-CYCLE]",
                "[LEVEL-UP",
                "[SERVER-AGGRO]",
                "[SERVER-SHOUT]",
                "[AGGRO]",
                "[AGGRO-OBSERVE]",
                "[AI",
                "[AI-TRACE]",
                "[ROOM-RNG]",
                "[WANDER-RNG]",
                "[WANDER-AUDIT]",
                "[WANDER-SIM]",
                "[MazeSpawner]",
                "[ZoneSpawnManager]",
                "[BEHAVIOR]",
                "[DLL-HP]",
                "[DLL-HP-UNRESOLVED]",
                "[DLL-DEATH-UNRESOLVED]",
                "[DLL-HP-RAW]",
                "[DAMAGE-NATIVE-SLOTS]",
                "[NATIVE-DAMAGE-CONTRACT]",
                "[DAMAGE-LEVEL]",
                "[DAMAGE-CLASS-MOD]",
                "[ATTACK-TIMING]",
                "[LOOT-FALLBACK]",
                "[LOOT-AUTHORED]",
                "[LOOT-RNG]",
                "[RNG-INSTANCE]",
                "[ITEM-SERIALIZE]",
                "[GETSYNCH]",
                "[HP-SYNC]",
                "[SEND-UPDATE]",
                "[MOVE-SYNC]",
                "[ACTION-0x50-SYNC]",
                "[ACTION-SYNC]",
                "[HP-VERIFY]",
                "[SYNCH",
                "[REGEN]",
                "[COMBAT-TICK]",
                "[UDP-COMBAT",
                "[ZONE-INVULN]",
                "[ZONE-TRACK]",
                "[ZONE-IN]",
                "[CHESTS]",
                "[WORLD-ENTITIES]",
                "[INSTANCE]",
                "[MERCHANT-DIAG]",
                "[LOC]",
                "[POSSE]",
                "[POSSE-PROBE]",
                "[PVP]",
                "[PVP-DUEL]",
                "[PVP-MATCH]",
                "[DUELARENA]",
                "[GROUP-CH0B]",
                "[GROUP]",
                "[ADMIN]",
                "[ACCOUNT]",
                "[ItemStatDB]",
                "[WriteItem]",
                "[IG-INJECT]",
                "[INVENTORY-WRITEINIT]",
                "[WELL]",
                "[QUEST-AVAILABLE]",
                "[QUEST-ACCEPT]",
                "[QUEST-TURNIN]",
                "[QUEST-REWARDS]",
                "[QUEST-ITEM-REMOVE]",
                "[QUEST-QUERY]",
                "[QUEST-PROGRESS]",
                "[QUEST-ADD]",
                "[QUEST-COMPLETE]",
                "[QUEST-REMOVE]",
                "[DROP-RING]",
                "[DROP-AMULET]",
                "[DROP-WRITEINIT]",
                // Equip + inventory flow — needed to diagnose "ring disappeared
                // on equip". The filter blocks these from both server.log AND
                // Editor.log via FocusedLogHandler, so they're hidden by
                // default. Enable explicitly for the next test pass.
                "[INVENTORY]",
                "[INV-TRACK]",
                "[INV-SLOT]",
                "[INV-VALIDATOR]",
                "[INV-RESTORE]",
                "[EQUIP]",
                "[EQUIP-TRACK]",
                "[EQUIPMENT-INIT]",
                "[GIVE-STACKED]",
                "[GIVE-ON-ACCEPT-ITEM]",
                "[STATE]",
                "[SAVE]",
                // Merchant refresh path — for "merchant timer ran out, no new
                // items spawned" diagnosis.
                "[MerchantManager]",
                "[Merchant]",
                "[MERCHANT-REFRESH]",
                "[REFRESH]",
                "[STACK-SEARCH]"
            };

            foreach (string prefix in allowedPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (line.StartsWith("[ZONE-JOIN]", StringComparison.OrdinalIgnoreCase))
            {
                return line.IndexOf("Zone:", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("ZoneJoin", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("FIRST LOGIN", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("LATE JOINER", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Room RNG seed", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("already spawned", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("transition", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Using zone ID", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (line.StartsWith("[ZONE]", StringComparison.OrdinalIgnoreCase))
            {
                return line.IndexOf("ZONE TRANSITION", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("CHECKPOINT TELEPORT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Sent DISCONNECT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Sent CONNECT", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("CurrentZone", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Stopped tick coroutine", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("Cleared message queue", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return false;
        }

        private sealed class FocusedLogHandler : ILogHandler
        {
            private readonly ILogHandler _inner;

            public FocusedLogHandler(ILogHandler inner)
            {
                _inner = inner;
            }

            public void LogException(Exception exception, DungeonRunners.Engine.Object context)
            {
                _inner?.LogException(exception, context);
            }

            public void LogFormat(LogType logType, DungeonRunners.Engine.Object context, string format, params object[] args)
            {
                string condition = format;
                if (args != null && args.Length > 0)
                {
                    try
                    {
                        condition = string.Format(format, args);
                    }
                    catch
                    {
                        condition = format;
                    }
                }

                if (ShouldMirrorLog(condition, logType))
                    _inner?.LogFormat(logType, context, format, args);
            }
        }

        private static string ResolveClientLogsDir()
        {
            string overrideDir = Environment.GetEnvironmentVariable("DR_CLIENT_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return overrideDir;
            return @"C:\Dungeon Runners\Dungeon Runners Game\logs";
        }

        private static string ResolveServerLogPath(string fallbackLogDir)
        {
            string overridePath = Environment.GetEnvironmentVariable("DR_SERVER_LOG_FILE");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath;

            return Path.Combine(fallbackLogDir, "server.log");
        }

        private static string ResolveRepoRoot()
        {
            DirectoryInfo dir = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string script = Path.Combine(dir.FullName, "scripts", "start_packet_capture.ps1");
                if (File.Exists(script))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
