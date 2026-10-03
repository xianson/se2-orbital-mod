#!/usr/bin/env bash
# check_contract.sh - the cross-mod contracts between this mod and the Aerodynamics Mod (sibling folder), checked
# from source: both mods compile separately and meet only by reflection, so a rename or retype on one side compiles
# fine and fails silently in game. Every name this mod reaches in AeroMod must exist there with the expected shape,
# and the entry-source contract numbers must agree. SKIP (exit 0) when the aero mod's source is not next to us.
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
o=$(grep -o 'public const int AeroEntryContract = [0-9]*' Scripts/Simulation/Frames/EntryHost.cs | grep -o '[0-9]*$')
[ -n "$e" ] && [ "$e" = "$o" ] || bad "entry-source contract: aero $e, orbital $o"
grep -q 'public static int RegisterEntrySource(string owner, Delegate source) => AeroEntryFx.RegisterEntrySource' "$api" || bad "AeroApi.RegisterEntrySource no longer returns AeroEntryFx's contract"
grep -q 'public static int RegisterEntrySource(string owner, Delegate source)' "$api" || bad "AeroApi.RegisterEntrySource(string, Delegate) missing"
grep -q 'public static bool HasEntrySource(string owner, Delegate source)' "$api" || bad "AeroApi.HasEntrySource(string, Delegate) missing"
grep -q '"AeroMod.AeroApi"' Scripts/Simulation/Frames/EntryHost.cs || bad "EntryHost no longer finds AeroMod.AeroApi"
grep -q 'Func<Keen.VRage.DCS.Components.Entity, ValueTuple<Vector3, float>>' "$fx" || bad "aero's source delegate is no longer Func<Entity, (Vector3, float)>"
grep -q 'Func<Entity, ValueTuple<Vector3, float>>' Scripts/Simulation/Frames/EntryHost.cs || bad "orbital's source delegate is no longer Func<Entity, (Vector3, float)>"

# 2. every AeroMod member named in a GetMethod/GetField string here exists in the aero source (dev code included)
for f in $(grep -rl '"AeroMod\.' Scripts --include=*.cs); do
  for m in $(grep -o 'GetMethod("[A-Za-z]*"' "$f" | sed 's/GetMethod("//; s/"//' | sort -u); do
    case "$m" in RegisterEntrySource|HasEntrySource|TryGetFlow|WingTips|EntryStrength) ;; *) continue ;; esac
    grep -rq "public static [A-Za-z<>]* $m(" "$AERO" || bad "$f: AeroMod.$m not found (public static)"
  done
done
grep -rq '"AeroMod.AeroEntryFx", "External"' Scripts && bad "the retired AeroEntryFx.External is still poked"

[ $rc -eq 0 ] && echo "PASS  contract (aero API $a, entry source $e; reflected names)" || echo "FAIL  contract"
exit $rc
