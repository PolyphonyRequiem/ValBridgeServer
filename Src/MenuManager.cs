using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValBridgeServer
{
    /// <summary>
    /// Main-thread executor for menu automation. Tool handlers run on the GABP server's WORKER thread, but
    /// Unity scene loading + UI object creation (TextMeshPro Awake → Mesh.Internal_Create) MUST run on the
    /// main thread or the graphics device crashes with a fatal signal (learned the hard way 2026-07-01:
    /// invoking FejdStartup.LoadMainScene off-thread = "Graphics device is null" hard crash). So a tool
    /// enqueues a load request here and awaits the TaskCompletionSource; Update() (main thread) performs the
    /// FejdStartup mutation + LoadMainScene and completes the task. Mirrors MovementManager's pattern.
    /// </summary>
    public class MenuManager : MonoBehaviour
    {
        private static MenuManager? _instance;
        public static MenuManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("ValBridgeServer_MenuManager");
                    _instance = go.AddComponent<MenuManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private struct LoadRequest
        {
            public string WorldName;
            public string CharacterFilename;
            public string CharacterName;
            public FileHelpers.FileSource FileSource;
            public World World;
            public TaskCompletionSource<object> Tcs;
        }

        private readonly Queue<LoadRequest> _pending = new Queue<LoadRequest>();
        private readonly object _lock = new object();

        /// <summary>Off-thread: queue a resolved load request; returns a task completed on the main thread.</summary>
        public Task<object> EnqueueLoad(string worldName, string charFilename, string charName,
                                        FileHelpers.FileSource src, World world)
        {
            var tcs = new TaskCompletionSource<object>();
            lock (_lock)
            {
                _pending.Enqueue(new LoadRequest
                {
                    WorldName = worldName, CharacterFilename = charFilename, CharacterName = charName,
                    FileSource = src, World = world, Tcs = tcs
                });
            }
            return tcs.Task;
        }

        private void Update()
        {
            LoadRequest req;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                req = _pending.Dequeue();
            }

            try
            {
                var fejd = FejdStartup.instance;
                if (fejd == null) { req.Tcs.TrySetResult(new { success = false, error = "FejdStartup gone (left the menu?)." }); return; }

                // Commit profile (FejdStartup.OnCharacterStart happy path).
                PlatformPrefs.SetString("profile", req.CharacterFilename);
                Game.SetProfile(req.CharacterFilename, req.FileSource);

                // Commit world + start as SINGLEPLAYER (FejdStartup.OnWorldStart happy path, no UI toggles).
                Game.m_serverOptionsSummary = "";
                PlatformPrefs.SetString("world", req.WorldName);
                SetPrivateField(fejd, "m_world", req.World);
                SetPrivateField(fejd, "m_startingWorld", true);
                ZNet.SetServer(true, false, false, req.WorldName, "", req.World);
                ZNet.ResetServerHost();

                // Load the main scene via FejdStartup's own private method (now on the MAIN thread).
                MethodInfo loadMain = fejd.GetType().GetMethod("LoadMainScene",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (loadMain == null) { req.Tcs.TrySetResult(new { success = false, error = "FejdStartup.LoadMainScene not found." }); return; }
                loadMain.Invoke(fejd, null);

                ValBridgeServerPlugin.ModLogger.LogInfo(
                    $"MenuManager: loading world='{req.WorldName}' character='{req.CharacterName}' ({req.CharacterFilename}) on main thread.");
                req.Tcs.TrySetResult(new
                {
                    success = true,
                    loaded = new { world = req.WorldName, character = req.CharacterName, filename = req.CharacterFilename },
                    note = "Main scene loading; the player spawns after terrain/ObjectDB init (a few seconds). Poll get_player_state."
                });
            }
            catch (Exception ex)
            {
                req.Tcs.TrySetResult(new { success = false, error = ex.Message, stack = ex.StackTrace });
            }
        }

        private static void SetPrivateField(object obj, string name, object value)
        {
            FieldInfo f = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) throw new Exception($"field '{name}' not found on {obj.GetType().Name}");
            f.SetValue(obj, value);
        }
    }
}
