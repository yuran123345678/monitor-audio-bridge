#!/bin/bash
# 盯着 CoreAudio 设备表：只要多出/少掉设备（尤其是一个新的输入设备），就记一条到 .out/jack-watch.log
# 用途：验证 MacBook Pro 的 3.5mm 耳机口插上线之后，macOS 会不会把它当成麦克风输入。
cd "$(dirname "$0")/.." || exit 1
mkdir -p .out
LOG=".out/jack-watch.log"
: > "$LOG"

snapshot() {
  /usr/sbin/system_profiler SPAudioDataType -json 2>/dev/null | /usr/bin/python3 -c '
import sys, json
try:
    d = json.load(sys.stdin)
except Exception:
    print("parse-error")
    raise SystemExit
rows = []
for grp in d.get("SPAudioDataType", []):
    for i in grp.get("_items", []):
        rows.append("%-40s in=%s out=%s transport=%s" % (
            i.get("_name", "?"),
            i.get("coreaudio_device_input", "-"),
            i.get("coreaudio_device_output", "-"),
            i.get("coreaudio_device_transport", "-"),
        ))
print("\n".join(sorted(rows)))'
}

prev=""
deadline=$((SECONDS + 1800))
while [ $SECONDS -lt $deadline ]; do
  cur=$(snapshot)
  if [ -n "$cur" ] && [ "$cur" != "$prev" ]; then
    printf '\n--- %s ---\n%s\n' "$(date '+%H:%M:%S')" "$cur" >> "$LOG"
    prev="$cur"
  fi
  sleep 2
done
printf '\n== 监视结束 %s ==\n' "$(date '+%H:%M:%S')" >> "$LOG"
