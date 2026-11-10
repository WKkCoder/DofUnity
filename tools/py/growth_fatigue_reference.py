#!/usr/bin/env python3
"""疲劳每日恢复判定的独立读法与黄金向量(r15-growth)。

这个脚本不看 C# 实现,只照服务端 CheckDailyScheduleTime(地址 0x0846c0a8)的反编译逐句写成 Python:
    边界 = mktime(今天 hour:00:00);now 的小时 < hour 时再减 86400 秒;到点 = (上次 <= 边界)。
用固定时区(没有夏令时)生成一批 (hour, now, last, due) 向量,写进 C# 测试用的夹具
(src/Dnf.Game.Tests/FatigueGoldenVectors.cs),由 FatigueGoldenTests 逐条对 FatigueRules.DueForReset。

观察 / 推断 / 未知:
    观察:判定与减 86400 秒的写法如上(反编译);登录读档(Inter_LoadEtc::SetFatigue 0x084c311a)与在线整点重置(GameWorld::ResetAllFatigue 0x086cc3ec)都走它。
    推断:服务端时区没有夏令时,所以"减 86400 秒"与 C# 的"减一个日历日"等价;这里固定用无夏令时的时区生成向量。
    未知:服务端实际的恢复整点(服务端配置,读不到);C# 默认的 6 点是推断,向量里把整点当参数枚举。

用法:
    python3 tools/py/growth_fatigue_reference.py            # 校验夹具与这里生成的一致(不一致退出码 1)
    python3 tools/py/growth_fatigue_reference.py --write    # 重新生成夹具
"""
import os
import sys
import time

os.environ["TZ"] = "Asia/Shanghai"
time.tzset()

HOURS = (0, 6, 12, 23)
DAYS = ((2026, 3, 10), (2026, 12, 31), (2027, 2, 28))
STAMP = "%Y%m%d%H%M%S"
FIXTURE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Dnf.Game.Tests", "FatigueGoldenVectors.cs")


def epoch(y, mo, d, h, mi, s):
    return int(time.mktime((y, mo, d, h, mi, s, 0, 0, -1)))


def server_check_daily(hour, last, now):
    """CheckDailyScheduleTime(hour, last, now) 的逐句翻译。"""
    t = time.localtime(now)
    boundary = epoch(t.tm_year, t.tm_mon, t.tm_mday, hour, 0, 0)
    if t.tm_hour < hour:
        boundary -= 86400
    return last <= boundary


def stamp(value):
    return "0" if value == 0 else time.strftime(STAMP, time.localtime(value))


def vectors():
    rows = []
    for hour in HOURS:
        for (y, mo, d) in DAYS:
            day_start = epoch(y, mo, d, 0, 0, 0)
            nows = {
                day_start,
                day_start + 12 * 3600,
                day_start + 86400 - 1,
                day_start + hour * 3600,
                day_start + hour * 3600 + 1,
                day_start + hour * 3600 - 1,
            }
            for now in sorted(nows):
                t = time.localtime(now)
                boundary = epoch(t.tm_year, t.tm_mon, t.tm_mday, hour, 0, 0)
                if t.tm_hour < hour:
                    boundary -= 86400
                lasts = {0}
                for off in (-3 * 86400, -86400 - 1, -86400, -86400 + 1, -1, 0):
                    lasts.add(now + off)
                for off in (-1, 0, 1):
                    lasts.add(boundary + off)
                for last in sorted(lasts):
                    rows.append((hour, now, last, server_check_daily(hour, last, now)))
    return rows


def render():
    lines = []
    for hour, now, last, due in vectors():
        lines.append("%d|%s|%s|%d" % (hour, stamp(now), stamp(last), 1 if due else 0))
    body = "\n".join("        " + line for line in lines)
    return (
        "// 本文件由 tools/py/growth_fatigue_reference.py 生成(--write),不要手改。\n"
        "// 每行 恢复整点|现在|上次(0 表示从没记过)|是否到点,时刻是 yyyyMMddHHmmss 的本地时间,全是合成的,与原版数据无关。\n"
        "namespace Dnf.Game.Tests;\n"
        "\n"
        "/// <summary>按服务端 CheckDailyScheduleTime 的反编译独立写出来的黄金向量(见生成脚本的说明)。</summary>\n"
        "internal static class FatigueGoldenVectors\n"
        "{\n"
        "    public const string Lines = \"\"\"\n"
        + body
        + "\n        \"\"\";\n"
        "}\n"
    )


def main(argv):
    text = render()
    if "--write" in argv:
        with open(FIXTURE, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print("已写入 %d 条向量" % (text.count("\n") - 10))
        return 0
    try:
        with open(FIXTURE, encoding="utf-8") as f:
            current = f.read()
    except OSError:
        print("没有夹具,先用 --write 生成")
        return 1
    rows = vectors()
    due = sum(1 for r in rows if r[3])
    print("向量 %d 条,其中到点 %d 条,不到点 %d 条" % (len(rows), due, len(rows) - due))
    if current != text:
        print("夹具与独立读法不一致,用 --write 重新生成并检查差异")
        return 1
    print("夹具与独立读法一致")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
