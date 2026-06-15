using System;
using System.IO;
using System.Reflection;
using System.Threading;
using DungeonRunners.Engine;
using DungeonRunners.Runtime;
using DungeonRunners.Core;

namespace DungeonRunners
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            SetDefaultEnv("DR_SERVER_DISABLE_SELF_WIRE_CAPTURE", "0");
            SetDefaultEnv("DR_SERVER_DISABLE_FOCUSED_LOGS", "1");
            SetDefaultEnv("DR_SERVER_VERBOSE_EVIDENCE_LOGS", "1");
            SetDefaultEnv("DR_SERVER_LAZY_ENCOUNTER_SPAWN", "1");
            SetDefaultEnv("DR_SERVER_DISABLE_WANDER", "0");

            string dataPath = ResolveDataPath();
            string persistentPath = ResolvePersistentPath(dataPath);
            EngineRuntime.DataPath = dataPath;
            EngineRuntime.PersistentDataPath = persistentPath;

            string logPath = Path.Combine(persistentPath, "logs", "dr_server_dotnet.log");
            ServerLog.Init(logPath);

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Debug.Log($"[BOOT] Dungeon Runners .NET 10 server — dataPath={dataPath}");
            Debug.Log($"[BOOT] persistentDataPath={persistentPath}");
            Debug.Log($"[BOOT] runtime={Environment.Version}  framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            Debug.Log($"[BOOT] DR_SERVER_DISABLE_WANDER={Environment.GetEnvironmentVariable("DR_SERVER_DISABLE_WANDER")} (diagnostic: monster wander room-RNG draws suppressed)");

            ServerConfig config = BuildConfig();

            var rootGo = new GameObject("ServerHost");
            var serverHost = rootGo.AddComponent<ServerHost>();
            InjectPrivateField(serverHost, "serverConfig", config);

            bool quitRequested = false;
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quitRequested = true; Debug.Log("[BOOT] Ctrl+C — shutting down"); };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            double last = 0.0;
            double fixedAccum = 0.0;
            const double targetFrame = 1.0 / 60.0;

            while (!quitRequested && !EngineRuntime.QuitRequested)
            {
                double now = sw.Elapsed.TotalSeconds;
                double dt = now - last;
                last = now;
                if (dt > 0.25) dt = 0.25;

                EngineRuntime.Tick((float)dt, (float)now);

                fixedAccum += dt;
                int guard = 0;
                while (fixedAccum >= EngineRuntime.FixedDeltaTime && guard++ < 8)
                {
                    EngineRuntime.FixedTick();
                    fixedAccum -= EngineRuntime.FixedDeltaTime;
                }

                double frameSpent = sw.Elapsed.TotalSeconds - now;
                double sleep = targetFrame - frameSpent;
                if (sleep > 0.0) Thread.Sleep((int)(sleep * 1000.0));
            }

            Debug.Log("[BOOT] Shutdown sequence");
            EngineRuntime.Shutdown();
            return EngineRuntime.ExitCode;
        }

        private static ServerConfig BuildConfig()
        {
            var c = ScriptableObject.CreateInstance<ServerConfig>();
            c.name = "ServerConfig";
            c.authServerIP = "0.0.0.0";
            c.authServerPort = 2110;
            c.gameServerIP = "127.0.0.1";
            c.gameServerPort = 2603;
            c.gameServerName = "Dungeon Runners Server";
            c.blowfishKey = "[;'.]94-31==-%&@!^+]";
            c.desKey = "TEST";
            c.maxPlayers = 100;
            c.serverVersion = "1.0.0";
            c.enableDebugLogging = true;
            c.defaultWorldId = 1;
            c.defaultSpawnPosition = new Vector3(100, 0, 100);
            c.defaultZoneId = 1;
            return c;
        }

        private static void SetDefaultEnv(string key, string value)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }

        private static void InjectPrivateField(object target, string fieldName, object value)
        {
            var f = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (f != null) f.SetValue(target, value);
            else Debug.LogError($"[BOOT] field '{fieldName}' not found on {target.GetType().Name}");
        }

        private static string ResolveDataPath()
        {
            string env = Environment.GetEnvironmentVariable("DR_DATA_PATH");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir != null; i++)
            {
                string dataRoot = Path.Combine(dir, "GameData");
                if (Directory.Exists(Path.Combine(dataRoot, "Database")))
                {
                    string assets = Path.Combine(dataRoot, "Assets");
                    Directory.CreateDirectory(assets);
                    return assets;
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return AppContext.BaseDirectory;
        }

        private static string ResolvePersistentPath(string dataPath)
        {
            string env = Environment.GetEnvironmentVariable("DR_PERSISTENT_PATH");
            if (!string.IsNullOrEmpty(env)) { Directory.CreateDirectory(env); return env; }

            string serverNetRoot = Path.GetDirectoryName(Path.GetDirectoryName(dataPath.TrimEnd(Path.DirectorySeparatorChar)));
            string p = serverNetRoot ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(p);
            return p;
        }
    }
}
