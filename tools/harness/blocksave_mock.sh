#!/usr/bin/env bash
# blocksave_mock.sh - a mock DevHarness for test_blocksave.sh offline (no game): state in $M, replies into $ST as "> cmd  =>  reply".
# usage: M=/tmp/bs; rm -rf $M; mkdir -p $M/logs; echo "-start:Orbital Test X -loadScripts" > $M/args.txt
#        M=$M ST=$M/status.txt LOGDIR=$M/logs ORB=tools/harness/blocksave_mock.sh ARGS=$M/args.txt MOCKWORLD="Orbital Test X" DRY=1 tools/harness/test_blocksave.sh
# MOCKBREAK=1: the reload loses the thruster fraction (the script must FAIL); MOCKWORLD: the world the "game" says it loaded.
M=${M:?}; mkdir -p "$M/logs"
log() { ls -t "$M"/logs/SpaceEngineers2_*[0-9].log 2>/dev/null | head -1; }
get() { cat "$M/$1" 2>/dev/null || echo 0; }
reply() { echo "$(date +%H:%M:%S) > $1  =>  $2" >> "$ST"; }
init_blocks() {
cat > "$M/blocks.txt" <<B
surface@-4,2,10=max:20;trim:0;p:1;y:1;r:1;rb:0
surface@4,2,10=max:20;trim:0;p:1;y:1;r:1;rb:0
gear@0,-3,5=down:1;shock:1;rb:0
airbrake@0,4,-8=max:35;rb:0
thrust@0,0,-20=f:0.5;rb:0
thrust@2,0,-20=f:0.5;rb:0
B
}
for c in "$@"; do
  case "$c" in
    __launch)
      sleep 1; n=$(( $(ls "$M"/logs | wc -l) + 1 )); f="$M/logs/SpaceEngineers2_$n.log"
      echo "x - Main Thread ->  Running application with arguments: -start:${MOCKWORLD:-Orbital Test SSTO Blocksave} -projectPaths:C:\x -loadScripts" > "$f"
      echo "[ORBIT] beacon Root Entity" >> "$f"
      : > "$ST"; echo 0 > "$M/written"
      if [ -f "$M/saved.txt" ]; then
        if [ -n "$MOCKBREAK" ]; then sed 's/f:0.73/f:0.5/' "$M/saved.txt"; else cat "$M/saved.txt"; fi | sed 's/rb:0/rb:1/' > "$M/blocks.txt"
        wc -l < "$M/blocks.txt" | tr -d ' ' > "$M/restored"; echo 0 > "$M/legacy"
      else init_blocks; echo 0 > "$M/restored"; echo 2 > "$M/legacy"; fi
      rm -f "$M/dumpwant" ;;
    findgrids) reply "$c" "grids: 1000000001=SSTO 1000000002=Other_Ship(static)" ;;
    "aeroget AeroBlockSave.Restored") reply "$c" "AeroMod.AeroBlockSave.Restored=$(get restored)" ;;
    "aeroget AeroBlockSave.Written") reply "$c" "AeroMod.AeroBlockSave.Written=$(get written)" ;;
    "aeroget AeroBlockSave.LegacyClaimed") reply "$c" "AeroMod.AeroBlockSave.LegacyClaimed=$(get legacy)" ;;
    "aeroget AeroBlockSave.Counts") reply "$c" "AeroMod.AeroBlockSave.Counts=restored $(get restored) written $(get written) legacy $(get legacy)" ;;
    "aeroget AeroBlockSave.Last") reply "$c" "AeroMod.AeroBlockSave.Last=mock" ;;
    "aeroset AeroBlockSave.DevDump "*) echo "${c#aeroset AeroBlockSave.DevDump }" > "$M/dumpwant"; reply "$c" "AeroMod.AeroBlockSave.DevDump=asked: the server step answers - read it again" ;;
    "aeroget AeroBlockSave.DevDump")
      g=$(cat "$M/dumpwant" 2>/dev/null)
      if [ "$g" = SSTO ]; then a="dump 'SSTO': $(wc -l < "$M/blocks.txt" | tr -d ' ') block(s) on 1 grid(s): $(tr '\n' ' ' < "$M/blocks.txt")"; else a="dump '$g': 0 block(s) on 0 grid(s):"; fi
      reply "$c" "AeroMod.AeroBlockSave.DevDump=$a" ;;
    "aeroset AeroBlockSave.DevSet "*)
      IFS='|' read -r g k w v <<<"${c#aeroset AeroBlockSave.DevSet }"
      case "$k" in surface-trim) bk=surface f=trim ;; surface-max) bk=surface f=max ;; gear) bk=gear f=down ;; airshock) bk=gear f=shock ;; airbrake-max) bk=airbrake f=max ;; thrust) bk=thrust f=f ;; esac
      case "$v" in up|off) v=0 ;; down|on) v=1 ;; esac
      if grep -q "^$bk@$w=" "$M/blocks.txt"; then
        before=$(grep "^$bk@$w=" "$M/blocks.txt")
        sed -i "/^$bk@$w=/ s/\([=;]\)$f:[^;]*/\1$f:$v/" "$M/blocks.txt"
        echo "set $before -> $(grep "^$bk@$w=" "$M/blocks.txt" | cut -d= -f2)" > "$M/setans"
      else echo "no $bk at $w on '$g'" > "$M/setans"; fi
      reply "$c" "AeroMod.AeroBlockSave.DevSet=asked: the server step answers - read it again" ;;
    "aeroget AeroBlockSave.DevSet") reply "$c" "AeroMod.AeroBlockSave.DevSet=$(cat "$M/setans")" ;;
    "save "*)
      cp "$M/blocks.txt" "$M/saved.txt"; echo $(( $(get written) + $(wc -l < "$M/blocks.txt") )) > "$M/written"
      echo "x [ORBIT-DEV] save -> '${c#save }': Success" >> "$(log)"; reply "$c" "saving to '${c#save }'" ;;
    *) reply "$c" "ok" ;;
  esac
done
tail -n 14 "$ST"
