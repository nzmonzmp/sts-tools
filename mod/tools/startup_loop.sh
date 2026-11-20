#!/bin/bash
# Usage: startup_loop.sh N [dossier]  — lance le jeu N fois via Steam, s'arrête au premier gel (jeu laissé figé pour l'examiner)
D=~/snap/steam/common/.local/share/SlayTheSpire2
OUT=${2:-./startup-freeze}   # dossier où copier le log et les threads du jeu figé
mkdir -p $OUT
close() {
  pkill -TERM -x SlayTheSpire2; for i in $(seq 30); do pgrep -x SlayTheSpire2 >/dev/null || break; sleep 1; done
  if pgrep -x SlayTheSpire2 >/dev/null; then pkill -KILL -x SlayTheSpire2; sleep 2; rm -f $D/StsTools/reports/session/running; fi
  pkill -TERM -f "[S]lay the Spire 2/crashpad_handler"; pkill -TERM -x steamerrorrepor
  for i in $(seq 20); do ps -eo args | grep -q "[A]ppId=2868840" || break; sleep 1; done
}
for n in $(seq $1); do
  close
  if ps -eo args | grep -q "[A]ppId=2868840" || pgrep -x SlayTheSpire2 >/dev/null; then echo "$n WRAPPER STILL ALIVE"; exit 1; fi
  sleep 3; t0=$(date +%s)
  (setsid steam steam://rungameid/2868840 >/dev/null 2>&1 &)
  for i in $(seq 40); do pgrep -x SlayTheSpire2 >/dev/null && break; sleep 1; done
  if ! pgrep -x SlayTheSpire2 >/dev/null; then (setsid steam steam://rungameid/2868840 >/dev/null 2>&1 &); for i in $(seq 40); do pgrep -x SlayTheSpire2 >/dev/null && break; sleep 1; done; fi
  pid=$(pgrep -x SlayTheSpire2) || { echo "$n NOT LAUNCHED"; exit 1; }
  ok=0
  for i in $(seq 90); do
    if [ $(stat -c %Y $D/logs/godot.log) -ge $t0 ] && grep -q "Time to main menu:" $D/logs/godot.log; then ok=1; break; fi
    sleep 1
  done
  task=$(grep -c "Invalid Task" $D/logs/godot.log)
  if [ $ok = 1 ]; then echo "$n OK $(grep 'Time to main menu:' $D/logs/godot.log | tail -1 | sed 's/.*: //') invalidTask=$task"; sleep 5; continue; fi
  echo "$n FROZEN pid=$pid invalidTask=$task"
  cp $D/logs/godot.log $OUT/godot.log
  for t in /proc/$pid/task/*; do echo "$(basename $t) $(cat $t/comm) $(awk '{print $3}' $t/stat) $(cat $t/wchan 2>/dev/null)"; done > $OUT/threads.txt
  tail -5 $D/logs/godot.log
  exit 2
done
close; sleep 3; (setsid steam steam://rungameid/2868840 >/dev/null 2>&1 &)
echo DONE
