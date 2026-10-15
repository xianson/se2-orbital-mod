#!/usr/bin/env bash
# check_contract.sh - the cross-mod contracts between this mod and the Aerodynamics Mod (sibling folder), checked
# from source: both mods compile separately and meet only by reflection, so a rename or retype on one side compiles
# fine and fails silently in game. Every name this mod reaches in AeroMod must exist there with the expected shape,
# and the entry-source contract numbers must agree; both ways (2026-10-14): every member either mod reaches in the other
# is declared with the parameter list the caller binds, every lookup passes its parameter types, neither API overloads a
# name. SKIP (exit 0) when the aero mod's source is not next to us.
cd "$(dirname "$0")/.."
AERO="../Aerodynamics Mod/Scripts"
[ -d "$AERO" ] || { echo "SKIP  contract (no Aerodynamics Mod source beside this one)"; exit 0; }
fx="$AERO/Simulation/Integration/AeroEntryFx.cs"
api="$AERO/Simulation/Integration/AeroApi.cs"
rc=0; bad() { rc=1; echo "      $1"; }

# 1. the entry source (EntryHost.cs <-> AeroApi.cs, the aero mod's public API). Two numbers: the API's version (members
#    are only added, so orbital needs at least the version it was written for) and the entry source's own contract
#    (what RegisterEntrySource returns; EntryHost hooks only on an exact match).
NEED_API=2   # (the oldest AeroApi.Version with every member orbital reaches)
a=$(grep -o 'public const int Version = [0-9]*' "$api" | grep -o '[0-9]*$')
[ -n "$a" ] && [ "$a" -ge "$NEED_API" ] || bad "aero API version $a, orbital needs >= $NEED_API"
e=$(grep -o 'public const int EntrySourceContract = [0-9]*' "$fx" | grep -o '[0-9]*$')
o=$(grep -o 'public const int AeroEntryContract = [0-9]*' Scripts/Simulation/Entry/EntryHost.cs | grep -o '[0-9]*$')
[ -n "$e" ] && [ "$e" = "$o" ] || bad "entry-source contract: aero $e, orbital $o"
grep -q 'public static int RegisterEntrySource(string owner, Delegate source) => AeroEntryFx.RegisterEntrySource' "$api" || bad "AeroApi.RegisterEntrySource no longer returns AeroEntryFx's contract"
grep -q 'public static int RegisterEntrySource(string owner, Delegate source)' "$api" || bad "AeroApi.RegisterEntrySource(string, Delegate) missing"
grep -q 'public static bool HasEntrySource(string owner, Delegate source)' "$api" || bad "AeroApi.HasEntrySource(string, Delegate) missing"
grep -q '"AeroMod.AeroApi"' Scripts/Simulation/Entry/EntryHost.cs || bad "EntryHost no longer finds AeroMod.AeroApi"
grep -q 'Func<Keen.VRage.DCS.Components.Entity, ValueTuple<Vector3, float>>' "$fx" || bad "aero's source delegate is no longer Func<Entity, (Vector3, float)>"
grep -q 'Func<Entity, ValueTuple<Vector3, float>>' Scripts/Simulation/Entry/EntryHost.cs || bad "orbital's source delegate is no longer Func<Entity, (Vector3, float)>"

# 2. every AeroMod member named in a GetMethod/GetField string here exists in the aero source (dev code included)
NAMES="RegisterEntrySource HasEntrySource TryGetFlow WingTips EntryStrength TryGetFlight TryGetForces TryGetLift TryGetGroundHeight PredictHullForces RequestForceTable TryGetEntry TryGetAtmosphere TryGetAtmosphereAt GetFxConsistency TryGetStructure SetFocusGrid TryGetStall TryGetLoac NoteMoved Build GetGravityDirection TryDesign GetTopSpeed GetTopSpeedAt GetThrustAlong TryGetCenterOfMass GetMass GetDesignPressures GetControlRatio GetWavePeak"
for f in $(grep -rl '"AeroMod\.' Scripts --include=*.cs); do
  for m in $(grep -o 'GetMethod("[A-Za-z]*"' "$f" | sed 's/GetMethod("//; s/"//' | sort -u); do
    case " $NAMES " in *" $m "*) ;; *) continue ;; esac
    grep -rq "public static [A-Za-z0-9_<>,]* $m(" "$AERO" || bad "$f: AeroMod.$m not found (public static)"
  done
done
grep -rq '"AeroMod.AeroEntryFx", "External"' Scripts && bad "the retired AeroEntryFx.External is still poked"

# 3. SIGNATURES (2026-10-14): every member either mod reaches in the other, declared with exactly the parameter list the
#    caller binds (its typed delegate, its typeof list, or the Invoke argument array's shape). sig: a declaration's parameter
#    types, names dropped, namespaces stripped ("Entity, out Vector3, out float"). A change here is a contract change: members
#    are never changed nor overloaded - the new shape gets a new name (TryGetEntryState2), the old one stays.
sig() {
  awk -v n="$2" 'BEGIN{re="public static [^(=]* " n "[(]"} $0 ~ re {on=1; buf=""} on {buf = buf " " $0; if (index($0, ")")) {print buf; exit}}' "$1" \
  | sed 's/^[^(]*(//; s/).*//' | tr ',' '\n' | sed 's/^ *//; s/ *$//; s/ [A-Za-z_][A-Za-z0-9_]*$//; s/[A-Za-z0-9_]*\.//g; s/  */ /g' | paste -sd',' - | sed 's/,/, /g'
}
want() { local got; got=$(sig "$1" "$2"); [ "$got" = "$3" ] || bad "$(basename "$1") $2($got): $4 binds $2($3)"; }
OAPI=Scripts/Simulation/Api/OrbitalApi.cs
# Orbital -> Aero (AeroApi), shipped: the entry source and aerobraking (EntryHost), the move push (ServerFrames)
want "$api" RegisterEntrySource "string, Delegate" EntryHost
want "$api" HasEntrySource "string, Delegate" EntryHost
want "$api" PredictHullForces "Entity, Vector3, float, float, out Vector3, out Vector3, out float" EntryHost.MeasureBeta
want "$api" RequestForceTable "Entity" EntryHost.MeasureBeta
want "$api" TryGetAtmosphereAt "Vector3D, out float, out float, out float, out float" EntryHost.SampleBorder
want "$api" NoteMoved "Entity" ServerFrames.CallNoteMoved
# ... the build hints and flight gauges (BuildHints, aero-2c's: bound by name; these are its Invoke arrays' shapes)
want "$api" TryGetFlight "Entity, out Vector3, out float, out float, out float, out float" "BuildHints / DevReentry / GridGen"
want "$api" TryGetForces "Entity, out Vector3, out Vector3, out float, out float, out float" "BuildHints / DevReentry / GridGen"
want "$api" SetFocusGrid "Entity" BuildHints
want "$api" TryGetStructure "Entity, out float, out float, out float" BuildHints
want "$api" TryGetStall "Entity, out int, out float, out float, out float" BuildHints
want "$api" TryGetLoac "Entity, out int, out float" BuildHints
want "$api" TryDesign "Entity, Vector3, float, out Vector3, out Vector3, out Vector3, out float, out float, out float" BuildHints
want "$api" GetTopSpeed "Entity, Vector3" BuildHints
want "$api" GetTopSpeedAt "Entity, Vector3, float" "BuildHints / FlyTest"
want "$api" GetThrustAlong "Entity, Vector3, float, out Vector3" BuildHints
want "$api" TryGetCenterOfMass "Entity, out Vector3" BuildHints
want "$api" GetMass "Entity" BuildHints
want "$api" GetDesignPressures "Entity, Vector3, float, List<Vector3>, List<Vector3>, List<float>, List<float>, int" BuildHints
want "$api" GetControlRatio "Entity, Vector3, float" BuildHints
# ... dev (the harness, the reentry test, generated grids, the fly test)
want "$api" TryGetLift "Entity, out Vector3, out float" GridGen
want "$api" TryGetGroundHeight "Entity, out float" "GridGen / FlyTest"
want "$api" TryGetEntry "Entity, out float, out bool" DevReentry
want "$api" TryGetAtmosphere "Entity, out float, out float, out float, out float" DevReentry
want "$api" GetFxConsistency "bool" "DevHarness fxcheck"
want "$AERO/Simulation/Integration/FlightFixture.cs" Build "Entity, string" "DevHarness flightdump"
want "$AERO/Simulation/Integration/PhysicsHack.cs" GetGravityDirection "DEntityContext" "DevHarness gridup"
# Aero -> Orbital (OrbitalApi): the railed entry's heat and loads (OrbitalLink, AeroEntryFx), the contact balance (OrbitalLink)
want "$OAPI" TryGetEntryState "Entity, out Vector3, out float" AeroEntryFx.EntryStateAt
want "$OAPI" TryGetEntryState2 "Entity, out Vector3, out float, out float, out Vector3" OrbitalLink.TryGetEntryState2
want "$OAPI" TryGetGridEdits "Entity, out Vector3D, out long" OrbitalLink.TryGetGridEdits

# 4. EXPLICIT PARAMETER TYPES: every cross-mod lookup passes them (a later overload must never make one ambiguous). BuildHints
#    is aero-2c's: reported, not failed, until it passes them too.
warn=""
for f in $(grep -rl '"AeroMod\.' Scripts --include=*.cs); do
  while IFS= read -r line; do
    for one in $(echo "$line" | grep -o 'GetMethod("[A-Za-z]*"' | sed 's/GetMethod("//; s/"//'); do
      case " $NAMES " in *" $one "*) ;; *) continue ;; esac
      echo "$line" | grep -q "GetMethod(\"$one\", *new\[\]" && continue
      if [ "$(basename "$f")" = BuildHints.cs ]; then warn="$warn $one"; else bad "$f: GetMethod(\"$one\") without its parameter types"; fi
    done
  done < <(grep 'GetMethod("' "$f")
done
for n in TryGetEntryState TryGetEntryState2 TryGetGridEdits; do
  grep -rqE "(GetMethod|Find)\(\"$n\", *new\[\]" "$AERO" || bad "aero's lookup of OrbitalApi.$n passes no parameter types (or is gone)"
done
[ -n "$warn" ] && echo "      (note: BuildHints.cs looks up by name only:$warn)"

# 5. NO OVERLOADS in either public API (a name-only lookup would throw AmbiguousMatchException)
for f in "$api" "$OAPI"; do
  d=$(grep -o 'public static [^(=]* [A-Za-z0-9_]*(' "$f" | sed 's/($//; s/.* //' | sort | uniq -d | tr '\n' ' ')
  [ -z "$d" ] || bad "$(basename "$f"): overloaded public member(s): $d"
done

[ $rc -eq 0 ] && echo "PASS  contract (aero API $a, entry source $e; names, signatures both ways, explicit lookups, no overloads)" || echo "FAIL  contract"
exit $rc
