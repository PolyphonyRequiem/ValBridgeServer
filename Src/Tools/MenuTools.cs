using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    /// <summary>
    /// Main-menu automation: load a world with a chosen character WITHOUT the human clicking the
    /// FejdStartup UI. Lets an agent driver take a client from the main menu straight into gameplay so the
    /// in-world tools (player/screenshot/etc.) become usable.
    ///
    /// <para>The actual load (scene change + UI object creation) is marshaled to Unity's MAIN THREAD via
    /// <see cref="MenuManager"/> — invoking FejdStartup.LoadMainScene on the GABP worker thread crashes the
    /// graphics device ("Graphics device is null", learned 2026-07-01). This tool only does thread-safe
    /// resolution (SaveSystem reads) then awaits the main-thread executor.</para>
    /// </summary>
    public class MenuTools
    {
        [Tool("list_saves", Description = "At the main menu, list the available worlds and characters (names) that load_world can use. Returns empty in-game.")]
        public object ListSaves()
        {
            try
            {
                var worlds = SaveSystem.GetWorldList()?.Select(w => w.m_name).ToList() ?? new List<string>();
                var profiles = SaveSystem.GetAllPlayerProfiles()?
                    .Select(p => new { filename = p.GetFilename(), name = p.GetName() }).ToList();
                bool atMenu = FejdStartup.instance != null && Player.m_localPlayer == null;
                return new { success = true, atMainMenu = atMenu, worlds, characters = profiles };
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message };
            }
        }

        [Tool("load_world", Description = "From the MAIN MENU, load a world by name with a character by name (or filename), starting singleplayer gameplay. No-op if already in-game. Args: world (world name), character (character name or filename).")]
        public async Task<object> LoadWorld(string world, string character)
        {
            try
            {
                if (Player.m_localPlayer != null)
                    return new { success = false, error = "Already in-game; load_world only works at the main menu." };

                var fejd = FejdStartup.instance;
                if (fejd == null)
                    return new { success = false, error = "FejdStartup not available (not at the main menu?)." };

                if (string.IsNullOrEmpty(world)) return new { success = false, error = "world name is required." };
                if (string.IsNullOrEmpty(character)) return new { success = false, error = "character name/filename is required." };

                // ── Resolve the character profile (thread-safe reads): filename first, then display name ──
                List<PlayerProfile> profiles = SaveSystem.GetAllPlayerProfiles();
                if (profiles == null || profiles.Count == 0)
                    return new { success = false, error = "No player profiles found." };

                PlayerProfile profile =
                    profiles.FirstOrDefault(p => string.Equals(p.GetFilename(), character, StringComparison.OrdinalIgnoreCase))
                    ?? profiles.FirstOrDefault(p => string.Equals(p.GetName(), character, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                    return new
                    {
                        success = false,
                        error = $"Character '{character}' not found.",
                        available = profiles.Select(p => new { filename = p.GetFilename(), name = p.GetName() }).ToList()
                    };

                // ── Resolve the world by name (thread-safe read) ──
                List<World> worlds = SaveSystem.GetWorldList();
                World? target = worlds?.FirstOrDefault(w => string.Equals(w.m_name, world, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    return new
                    {
                        success = false,
                        error = $"World '{world}' not found.",
                        available = worlds?.Select(w => w.m_name).ToList()
                    };
                if (target.m_dataError != World.SaveDataError.None)
                    return new { success = false, error = $"World '{target.m_name}' has a save data error: {target.m_dataError}." };

                // ── Marshal the actual load to the MAIN THREAD (scene change + UI creation) and await it ──
                return await MenuManager.Instance.EnqueueLoad(
                    target.m_name, profile.GetFilename(), profile.GetName(), profile.m_fileSource, target);
            }
            catch (Exception ex)
            {
                return new { success = false, error = ex.Message, stack = ex.StackTrace };
            }
        }
    }
}
