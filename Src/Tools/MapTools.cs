using System;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    /// <summary>
    /// Map control for map-mod inspection. The region overlay only draws on the LARGE map (and the
    /// minimap), and nothing else exposes a way to open it — so an agent driver could get in-world but
    /// never see the map. These tools open/close the large map and toggle the minimap on the MAIN THREAD
    /// (Minimap UI mutation must not run on the GABP worker thread), via the existing MainThreadDispatcher.
    /// </summary>
    public class MapTools
    {
        [Tool("set_map_mode", Description = "Open or close Valheim's map. mode='large' opens the FULL map (needed to inspect the region overlay), 'small' shows just the minimap, 'none' hides it. Requires a world loaded and the map enabled (not 'nomap').")]
        public object SetMapMode(
            [ToolParameter(Description = "One of: 'large' (full map), 'small' (minimap only), 'none' (hidden).")] string mode = "large")
        {
            var tcs = new TaskCompletionSource<object>();
            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    var mm = Minimap.instance;
                    if (mm == null) { tcs.SetResult(new { success = false, error = "Minimap.instance is null (no world loaded, or map disabled via nomap)." }); return; }
                    if (Player.m_localPlayer == null) { tcs.SetResult(new { success = false, error = "No local player." }); return; }

                    // Force-clear the nomap gate: Minimap.SetMapMode overrides any mode to None when
                    // Game.m_noMap is true, and m_noMap is cached at load (console `nomap` toggles the
                    // per-char pref / global key but does NOT recompute the cached field live). For map-mod
                    // INSPECTION we always want the map openable, so clear the cache here. Also clears the
                    // per-character pref + global key so the state is coherent, not just the cached bool.
                    if (mode != null && !mode.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        Game.m_noMap = false;
                        try
                        {
                            var pn = Player.m_localPlayer.GetPlayerName();
                            PlatformPrefs.SetFloat("mapenabled_" + pn, 1f);
                            if (ZoneSystem.instance != null) ZoneSystem.instance.RemoveGlobalKey(GlobalKeys.NoMap);
                        }
                        catch { /* best-effort coherence; the cached bool is the load-bearing one */ }
                    }

                    Minimap.MapMode target;
                    switch ((mode ?? "large").ToLowerInvariant())
                    {
                        case "large": target = Minimap.MapMode.Large; break;
                        case "small": target = Minimap.MapMode.Small; break;
                        case "none":
                        case "off":
                        case "hidden": target = Minimap.MapMode.None; break;
                        default:
                            tcs.SetResult(new { success = false, error = $"Unknown mode '{mode}'. Use large|small|none." });
                            return;
                    }

                    mm.SetMapMode(target);
                    ValBridgeServerPlugin.ModLogger.LogInfo($"MapTools.set_map_mode -> {target}");
                    tcs.SetResult(new { success = true, mode = target.ToString() });
                }
                catch (Exception ex)
                {
                    tcs.SetResult(new { success = false, error = ex.Message });
                }
            });
            return tcs.Task.Result;
        }
    }
}
