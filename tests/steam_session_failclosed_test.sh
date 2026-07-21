#!/usr/bin/env bash
# Deterministic fail-closed contract test for SteamSessionTools.get_steam_session_state.
#
# WHAT THIS PROVES (deterministically, no live Steam client, no Valheim, headless CI-safe):
#   When the Steamworks native layer / SteamAPI is NOT initialized in the host process
#   (exactly the case on a headless build box), get_steam_session_state() MUST fail closed:
#     success == false
#     steamApiInitialized == false
#     bLoggedOn == false
#     subscribedToValheim == false
#     ownerIsSelf == false
#     subscribedFromFamilySharing == false
#     steamId64 == "0"  and  appOwnerSteamId64 == "0"
#     reason is a non-empty string
#
# It does NOT (and cannot, offline) test the happy path — that is covered by the runtime
# acceptance step in the kanban card (call the tool on the primary and valbot lanes).
#
# The tool reflectively-instantiated here references ONLY Steamworks.NET (com.rlabrecque.
# steamworks.net) + Newtonsoft.Json, so it loads without Unity/BepInEx. The Steamworks calls
# throw in the native layer (no steam_api lib / not initialized); the tool's try/catch converts
# every such failure into the fail-closed result above.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
export VALHEIM_MANAGED="${VALHEIM_MANAGED:-/home/polyphonyrequiem/valheim-managed}"
export BEPINEX_CORE="${BEPINEX_CORE:-/home/polyphonyrequiem/repos/SBPRValheimMods/.sdk/BepInExPack_Valheim/BepInEx/core}"

DLL="$PROJ_DIR/bin/Release/ValBridgeServer.dll"
if [[ ! -f "$DLL" ]]; then
  echo "[test] building Release first..." >&2
  dotnet build "$PROJ_DIR/ValBridgeServer.csproj" -c Release -v quiet >/dev/null
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Standalone net48 harness that loads the built tool by reflection and asserts the contract.
mkdir -p "$WORK/harness"
cat > "$WORK/harness/harness.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>failclosed_harness</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <Reference Include="ValBridgeServer"><HintPath>__DLL__</HintPath></Reference>
    <Reference Include="com.rlabrecque.steamworks.net"><HintPath>__STEAM__</HintPath></Reference>
  </ItemGroup>
</Project>
CSPROJ
sed -i "s|__DLL__|$DLL|; s|__STEAM__|$VALHEIM_MANAGED/com.rlabrecque.steamworks.net.dll|" "$WORK/harness/harness.csproj"

cat > "$WORK/harness/Program.cs" <<'CS'
using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class Program
{
    static int Main()
    {
        object result;
        try
        {
            var asm = Assembly.LoadFrom(System.IO.Path.GetFullPath(
                Environment.GetEnvironmentVariable("VALBRIDGE_DLL")));
            var type = asm.GetType("ValBridgeServer.Tools.SteamSessionTools", true);
            var inst = Activator.CreateInstance(type);
            var mi = type.GetMethod("GetSteamSessionState");
            result = mi.Invoke(inst, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[test] tool invocation itself threw (should have failed closed instead): " + ex);
            return 2;
        }

        var j = JObject.Parse(JsonConvert.SerializeObject(result));
        Console.WriteLine("[test] tool returned: " + j.ToString(Formatting.None));

        bool ok = true;
        void MustBeFalse(string k) { if ((bool?)j[k] != false) { Console.Error.WriteLine($"[FAIL] {k} != false"); ok = false; } }
        void MustBeZero(string k)  { if ((string)j[k] != "0")  { Console.Error.WriteLine($"[FAIL] {k} != \"0\""); ok = false; } }

        MustBeFalse("success");
        MustBeFalse("steamApiInitialized");
        MustBeFalse("bLoggedOn");
        MustBeFalse("subscribedToValheim");
        MustBeFalse("ownerIsSelf");
        MustBeFalse("subscribedFromFamilySharing");
        MustBeZero("steamId64");
        MustBeZero("appOwnerSteamId64");
        if (string.IsNullOrWhiteSpace((string)j["reason"])) { Console.Error.WriteLine("[FAIL] reason empty"); ok = false; }
        if ((long?)j["appId"] != 892970) { Console.Error.WriteLine("[FAIL] appId != 892970"); ok = false; }

        Console.WriteLine(ok ? "[PASS] fail-closed contract holds" : "[FAIL] fail-closed contract violated");
        return ok ? 0 : 1;
    }
}
CS

export VALBRIDGE_DLL="$DLL"
echo "[test] building + running fail-closed harness..." >&2
dotnet run --project "$WORK/harness/harness.csproj" -c Release -v quiet
