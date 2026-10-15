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
rc=0; bad() { rc=1; echo "      $1"; }
# src <tree> <File.cs>: the one file of that name anywhere under the tree (both mods move files between feature folders;
# the contract follows the name, not the folder). Zero or several matches stop the check: a guess would test the wrong file.
src() {
  local hits n; hits=$(find "$1" -name "$2" -type f 2>/dev/null); n=$(printf '%s' "$hits" | grep -c .)
  [ "$n" -eq 1 ] && { echo "$hits"; return 0; }
  { echo "FAIL  contract: $n files named $2 under $1"; [ -n "$hits" ] && echo "$hits" | sed 's/^/      /'; } >&2; return 1
}
fx=$(src "$AERO" AeroEntryFx.cs) || exit 1
api=$(src "$AERO" AeroApi.cs) || exit 1
fixture=$(src "$AERO" FlightFixture.cs) || exit 1
hack=$(src "$AERO" PhysicsHack.cs) || exit 1
EH=$(src Scripts EntryHost.cs) || exit 1
OAPI=$(src Scripts OrbitalApi.cs) || exit 1
# Both APIs are partial classes over several files (AeroApi.cs, AeroApi.Flight.cs, ...; OrbitalApi.cs, OrbitalApi.Grids.cs,
# ...): the signature and overload checks read every partial beside the main file (2026-10-15).
mapfile -t APIS < <(find "$(dirname "$api")" -maxdepth 1 -name 'AeroApi*.cs' -type f | sort)
mapfile -t OAPIS < <(find "$(dirname "$OAPI")" -maxdepth 1 -name 'OrbitalApi*.cs' -type f | sort)

# 1. the entry source (EntryHost.cs <-> AeroApi.cs, the aero mod's public API). Two numbers: the API's version (members
#    are only added, so orbital needs at least the version it was written for) and the entry source's own contract
#    (what RegisterEntrySource returns; EntryHost hooks only on an exact match).
NEED_API=2   # (the oldest AeroApi.Version with every member orbital reaches)
a=$(grep -o 'public const int Version = [0-9]*' "$api" | grep -o '[0-9]*$')
[ -n "$a" ] && [ "$a" -ge "$NEED_API" ] || bad "aero API version $a, orbital needs >= $NEED_API"
e=$(grep -o 'public const int EntrySourceContract = [0-9]*' "$fx" | grep -o '[0-9]*$')
o=$(grep -o 'public const int AeroEntryContract = [0-9]*' "$EH" | grep -o '[0-9]*$')
[ -n "$e" ] && [ "$e" = "$o" ] || bad "entry-source contract: aero $e, orbital $o"
grep -q 'public static int RegisterEntrySource(string owner, Delegate source) => AeroEntryFx.RegisterEntrySource' "$api" || bad "AeroApi.RegisterEntrySource no longer returns AeroEntryFx's contract"
grep -q 'public static int RegisterEntrySource(string owner, Delegate source)' "$api" || bad "AeroApi.RegisterEntrySource(string, Delegate) missing"
grep -q 'public static bool HasEntrySource(string owner, Delegate source)' "$api" || bad "AeroApi.HasEntrySource(string, Delegate) missing"
grep -q '"AeroMod.AeroApi"' "$EH" || bad "EntryHost no longer finds AeroMod.AeroApi"
grep -q 'Func<Keen.VRage.DCS.Components.Entity, ValueTuple<Vector3, float>>' "$fx" || bad "aero's source delegate is no longer Func<Entity, (Vector3, float)>"
grep -q 'Func<Entity, ValueTuple<Vector3, float>>' "$EH" || bad "orbital's source delegate is no longer Func<Entity, (Vector3, float)>"

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
# sig <name> <file...>: the first declaration of <name> in those files (no overloads, step 5, so the first is the only one).
sig() {
  local n="$1"; shift
  awk -v n="$n" 'BEGIN{re="public static [^(=]* " n "[(]"} $0 ~ re {on=1; buf=""} on {buf = buf " " $0; if (index($0, ")")) {print buf; exit}}' "$@" \
  | sed 's/^[^(]*(//; s/).*//' | tr ',' '\n' | sed 's/^ *//; s/ *$//; s/ [A-Za-z_][A-Za-z0-9_]*$//; s/[A-Za-z0-9_]*\.//g; s/  */ /g' | paste -sd',' - | sed 's/,/, /g'
}
# want <aero|orbital|file> <name> <params> <caller>: aero = every AeroApi*.cs, orbital = every OrbitalApi*.cs.
want() {
  local got where
  case "$1" in
    aero) got=$(sig "$2" "${APIS[@]}"); where=AeroApi ;;
    orbital) got=$(sig "$2" "${OAPIS[@]}"); where=OrbitalApi ;;
    *) got=$(sig "$2" "$1"); where=$(basename "$1") ;;
  esac
  [ "$got" = "$3" ] || bad "$where $2($got): $4 binds $2($3)"
}
# Orbital -> Aero (AeroApi), shipped: the entry source and aerobraking (EntryHost), the move push (ServerFrames)
want aero RegisterEntrySource "string, Delegate" EntryHost
want aero HasEntrySource "string, Delegate" EntryHost
want aero PredictHullForces "Entity, Vector3, float, float, out Vector3, out Vector3, out float" EntryHost.MeasureBeta
want aero RequestForceTable "Entity" EntryHost.MeasureBeta
want aero TryGetAtmosphereAt "Vector3D, out float, out float, out float, out float" EntryHost.SampleBorder
want aero NoteMoved "Entity" ServerFrames.CallNoteMoved
# ... the build hints and flight gauges (BuildHints, aero-2c's: bound by name; these are its Invoke arrays' shapes)
want aero TryGetFlight "Entity, out Vector3, out float, out float, out float, out float" "BuildHints / DevReentry / GridGen"
want aero TryGetForces "Entity, out Vector3, out Vector3, out float, out float, out float" "BuildHints / DevReentry / GridGen"
want aero SetFocusGrid "Entity" BuildHints
want aero TryGetStructure "Entity, out float, out float, out float" BuildHints
want aero TryGetStall "Entity, out int, out float, out float, out float" BuildHints
want aero TryGetLoac "Entity, out int, out float" BuildHints
want aero TryDesign "Entity, Vector3, float, out Vector3, out Vector3, out Vector3, out float, out float, out float" BuildHints
want aero GetTopSpeed "Entity, Vector3" BuildHints
want aero GetTopSpeedAt "Entity, Vector3, float" "BuildHints / FlyTest"
want aero GetThrustAlong "Entity, Vector3, float, out Vector3" BuildHints
want aero TryGetCenterOfMass "Entity, out Vector3" BuildHints
want aero GetMass "Entity" BuildHints
want aero GetDesignPressures "Entity, Vector3, float, List<Vector3>, List<Vector3>, List<float>, List<float>, int" BuildHints
want aero GetControlRatio "Entity, Vector3, float" BuildHints
# ... dev (the harness, the reentry test, generated grids, the fly test)
want aero TryGetLift "Entity, out Vector3, out float" GridGen
want aero TryGetGroundHeight "Entity, out float" "GridGen / FlyTest"
want aero TryGetEntry "Entity, out float, out bool" DevReentry
want aero TryGetAtmosphere "Entity, out float, out float, out float, out float" DevReentry
want aero GetFxConsistency "bool" "DevHarness fxcheck"
want "$fixture" Build "Entity, string" "DevHarness flightdump"
want "$hack" GetGravityDirection "DEntityContext" "DevHarness gridup"
# Aero -> Orbital (OrbitalApi): the railed entry's heat and loads (OrbitalLink, AeroEntryFx), the contact balance (OrbitalLink)
want orbital TryGetEntryState "Entity, out Vector3, out float" AeroEntryFx.EntryStateAt
want orbital TryGetEntryState2 "Entity, out Vector3, out float, out float, out Vector3" OrbitalLink.TryGetEntryState2
want orbital TryGetGridEdits "Entity, out Vector3D, out long" OrbitalLink.TryGetGridEdits

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
#    (over all of a class's partial files: an overload split across two partials is still one)
d=$(cat "${APIS[@]}" | grep -o 'public static [^(=]* [A-Za-z0-9_]*(' | sed 's/($//; s/.* //' | sort | uniq -d | tr '\n' ' ')
[ -z "$d" ] || bad "AeroApi: overloaded public member(s): $d"
d=$(cat "${OAPIS[@]}" | grep -o 'public static [^(=]* [A-Za-z0-9_]*(' | sed 's/($//; s/.* //' | sort | uniq -d | tr '\n' ' ')
[ -z "$d" ] || bad "OrbitalApi: overloaded public member(s): $d"

# 6. THE HARNESS'S FLAGS (2026-10-15, AUDIT item 7): every AeroMod static the dev code sets or reads by a literal string
#    (SetForeignFlag / SetForeignString / ForeignValue("AeroMod.Type[+Nested]", "Member"): DevHarness, GridGen) is a static
#    field or property of that type in the aero source - a rename there leaves the harness command answering "not found".
#    OPTIONAL: types that live only in a local, untracked aero file (absent from a clean checkout: noted, not failed).
OPTIONAL=" AeroSpeedSpike "
nflags=0; opt=""
while IFS= read -r hit; do
  t=$(echo "$hit" | sed -E 's/^[^"]*"AeroMod\.([A-Za-z0-9_.+]+)", *"([A-Za-z0-9_]+)".*/\1/')
  m=$(echo "$hit" | sed -E 's/^[^"]*"AeroMod\.([A-Za-z0-9_.+]+)", *"([A-Za-z0-9_]+)".*/\2/')
  leaf=${t##*[.+]}; nflags=$((nflags + 1))
  decl=$(grep -rlE "(class|struct) $leaf\b" "$AERO" --include=*.cs)
  if [ -z "$decl" ]; then
    case "$OPTIONAL" in *" $leaf "*) opt="$opt $t.$m" ;; *) bad "harness: AeroMod.$t not found (for .$m)" ;; esac
    continue
  fi
  found=""
  while IFS= read -r df; do grep -qE "static [^=(;]*\b$m\b *(=|;|\{)" "$df" && { found=y; break; }; done <<< "$decl"
  [ -n "$found" ] || bad "harness: AeroMod.$t.$m is no static field/property in the aero source"
done < <(grep -rhoE '(SetForeignFlag|SetForeignString|ForeignValue)\("AeroMod\.[A-Za-z0-9_.+]+", *"[A-Za-z0-9_]+"' Scripts --include=*.cs | sort -u)
[ -n "$opt" ] && echo "      (note: local-only aero type(s) absent, not checked:$opt)"

[ $rc -eq 0 ] && echo "PASS  contract (aero API $a, entry source $e; names, signatures both ways, explicit lookups, no overloads, $nflags harness flags)" || echo "FAIL  contract"
exit $rc
