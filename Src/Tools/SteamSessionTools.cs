using System;
using Lib.GAB.Tools;
using Steamworks;

namespace ValBridgeServer.Tools
{
    /// <summary>
    /// Authoritative, read-only Steamworks readiness/identity probe for dual-client automation.
    ///
    /// <para>WHY THIS EXISTS: no client-external signal (loginusers.vdf, registry.vdf ActiveUser,
    /// steam/webhelper argv, CM sockets, private IPC, process existence, URI dispatch) proves the
    /// conjunction of <b>correct account + live online authentication + Valheim entitlement</b> for the
    /// account the running Valheim process is actually using. Those are diagnostics only. The only
    /// authoritative source is the Steamworks API <b>inside the real AppID process</b> — which is exactly
    /// where this mod runs. This tool queries that API and returns non-secret QA identity data.</para>
    ///
    /// <para>FAIL CLOSED: if the Steamworks API is not initialized/usable, every field is reported as
    /// unknown/false with <c>success=false</c> and a reason. Identity is NEVER synthesized from persona
    /// names, process argv, ActiveUser, remembered-account files, or sockets. If Steamworks can't answer,
    /// the answer is "no".</para>
    ///
    /// <para>NEVER reads, returns, logs, or stores credentials, tokens, Steam Guard, or sentry state.
    /// SteamID64 and entitlement booleans are non-secret QA identity data.</para>
    ///
    /// <para>Uses only the third-party MIT-licensed Steamworks.NET public facade
    /// (com.rlabrecque.steamworks.net) — SteamAPI / ISteamUser / ISteamApps. No IronGate game internals
    /// are referenced.</para>
    /// </summary>
    public class SteamSessionTools
    {
        // Valheim's Steam AppID. Documented/public; the entitlement gate for this process.
        private const uint ValheimAppId = 892970u;

        [Tool("get_steam_session_state", Description =
            "Authoritative read-only Steamworks identity/readiness probe for the account THIS Valheim process " +
            "is using. Works at the main menu and in-world. Returns SteamID64, BLoggedOn, Valheim (892970) " +
            "subscription, app owner, and family-sharing status. Fails closed (success=false, all fields " +
            "unknown/false) if the Steam API is unavailable — never synthesizes identity from files/argv/sockets. " +
            "Non-secret QA identity data only; no credentials/tokens are ever read or returned.")]
        public object GetSteamSessionState()
        {
            // ── Fail-closed skeleton. Every field defaults to the safe/negative value. ──
            bool steamApiInitialized = false;
            bool bLoggedOn = false;
            string steamId64 = "0";
            bool subscribedToValheim = false;
            string appOwnerSteamId64 = "0";
            bool ownerIsSelf = false;
            bool subscribedFromFamilySharing = false;
            string reason;

            try
            {
                // Gate 1: is the Steam client process even running/reachable? Cheap, no init required.
                // If this throws (native lib missing) or returns false, we fail closed below.
                bool steamRunning;
                try { steamRunning = SteamAPI.IsSteamRunning(); }
                catch (Exception ex)
                {
                    return Fail($"Steamworks native layer unavailable: {ex.GetType().Name}: {ex.Message}");
                }
                if (!steamRunning)
                    return Fail("SteamAPI.IsSteamRunning() == false: the Steam client is not running or not reachable from this process.");

                // Gate 2: are the interface pointers live? Steamworks.NET facades throw
                // InvalidOperationException if SteamAPI_Init has not succeeded in this process.
                // Valheim initializes Steam at startup (menu included), so this normally succeeds
                // both at menu and in-world. Any throw => fail closed.
                CSteamID myId;
                try
                {
                    myId = SteamUser.GetSteamID();
                }
                catch (Exception ex)
                {
                    return Fail($"Steam interfaces not initialized in this process ({ex.GetType().Name}: {ex.Message}). " +
                                "SteamAPI_Init has not succeeded; identity is unknown.");
                }

                steamApiInitialized = true;
                steamId64 = myId.m_SteamID.ToString();

                // Authoritative online-auth signal: is this user logged on to Steam right now?
                bLoggedOn = SteamUser.BLoggedOn();

                // Entitlement gate for Valheim specifically.
                var valheimApp = new AppId_t(ValheimAppId);
                subscribedToValheim = SteamApps.BIsSubscribedApp(valheimApp);

                // Who OWNS this app license (distinguishes owned vs family-shared/borrowed).
                CSteamID owner = SteamApps.GetAppOwner();
                appOwnerSteamId64 = owner.m_SteamID.ToString();
                ownerIsSelf = owner.m_SteamID == myId.m_SteamID;

                // Is the CURRENT running app (Valheim) being played via Family Sharing?
                subscribedFromFamilySharing = SteamApps.BIsSubscribedFromFamilySharing();

                // Build a human-readable reason describing the readiness verdict.
                if (!bLoggedOn)
                    reason = "Steam API usable but user is NOT logged on (offline / auth not live).";
                else if (!subscribedToValheim)
                    reason = "Steam API usable and logged on, but this account is NOT subscribed to Valheim (892970).";
                else
                    reason = "Steam session ready: API initialized, user logged on, and subscribed to Valheim (892970).";

                return new
                {
                    success = true,
                    reason,
                    appId = ValheimAppId,
                    steamApiInitialized,
                    steamId64,
                    bLoggedOn,
                    subscribedToValheim,
                    appOwnerSteamId64,
                    ownerIsSelf,
                    subscribedFromFamilySharing
                };
            }
            catch (Exception ex)
            {
                // Any unexpected failure => fail closed. Do not leak partial/synthesized identity.
                return Fail($"Unexpected Steamworks probe failure: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Fail-closed result: success=false with every identity/entitlement field forced to its safe
        /// negative value. Callers must treat this as "session NOT proven".
        /// </summary>
        private object Fail(string reason)
        {
            return new
            {
                success = false,
                reason,
                appId = ValheimAppId,
                steamApiInitialized = false,
                steamId64 = "0",
                bLoggedOn = false,
                subscribedToValheim = false,
                appOwnerSteamId64 = "0",
                ownerIsSelf = false,
                subscribedFromFamilySharing = false
            };
        }
    }
}
