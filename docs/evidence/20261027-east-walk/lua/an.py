import json
import sys

path = sys.argv[1]
d = json.load(open(path, encoding="utf-8"))["value"]
print("startX", d["startX"])
for i, s in enumerate(d["segments"], 1):
    print("seg%d: target=%.0f x=%.1f y=%s completed=%s jumps=%s updates=%s grounded=%s"
          % (i, s["target"], s["x"], s["y"], s["completed"], s["jumps"], s["updates"], s["grounded"]))
    if not s["completed"]:
        print("   reason:", s.get("reason"))
    print("   nearby:", [(n["kind"], round(n["d"])) for n in s["nearby"]])
    rows = s.get("rows") or []
    if rows:
        # 以 AI 左边缘所在列为中心，显示右侧 12 列的地面轮廓
        tx = int(s["x"] // 16)
        print("   rows[%d] around col %d:" % (len(rows), tx))
