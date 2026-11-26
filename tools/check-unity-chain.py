#!/usr/bin/env python3
"""静态检查 tools/unity-build-town.sh 的两条铁律：
  (a) 凡带 -dnftimescale 的 \"$app\" 调用必带 -dnfquitaftersim <正数>；
  (b) 所有通过判据行与基线逐行一致（不允许删改或放松）。
用法:
  python3 tools/check-unity-chain.py [脚本路径]
  python3 tools/check-unity-chain.py --selftest
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_SCRIPT = os.path.join(ROOT, "tools", "unity-build-town.sh")

BASELINE_CRITERIA_LINES = (
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py out/town-demo-545.png out/town-cpu-545.png --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-$tag.png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$live_log"; then',
    'exit 1',
    'grep -q \'DNF_TOWN_DEMO ran \' "$live_log" || { echo "实时运行没有留下 DNF_TOWN_DEMO ran 行" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'grep -q \'DNF_TOWN_DEMO ran .* facing=left motion=Rest cameraX=0 leftEdge=True\' "$walk_log" \\',
    '|| { echo "自动行走后主角不在预期的状态(应停在可走区域左边界上、朝左、站立、镜头 0):$walk_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-$tag.png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'cli_hero="$(dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll combat-check --map map/lorien/s2.map --play 0.5:left,5:down,6.5:none --seconds 7 --no-damage \\',
    '[ -n "$cli_hero" ] || { echo "命令行 combat-check 没有给出主角的最终位置" >&2; exit 1; }',
    'grep -q -E "DNF_TOWN_DEMO ran .* hero=\\(${cli_hero}\\) facing=left motion=Rest cameraX=0 .*room=true" "$room_walk_log" \\',
    '|| { echo "房间里自动行走后主角不在命令行算出的位置 (${cli_hero}) 朝左、站立、镜头 0:$room_walk_log" >&2; exit 1; }',
    'case "$cli_hero" in *,348.0) ;; *) echo "主角应走到通行带的下沿(y = 348.0),命令行给出 ${cli_hero}" >&2; exit 1 ;; esac',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-fight-$(printf \'%.2f\' "$at").png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$fight_log"; then',
    'exit 1',
    'grep -q -E \'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 hits=[1-9][0-9]*\' "$fight_log" \\',
    '|| { echo "实时战斗里没有命中过怪物(应有 hits>=1):$fight_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-action-$(printf \'%.2f\' "$at").png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-skill-$(printf \'%.2f\' "$at").png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$skill_log"; then',
    'exit 1',
    'grep -q -E \'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 hits=[1-9][0-9]* .* casts=3 \' "$skill_log" \\',
    '|| { echo "实时放技能里 A、S、D 没有各放出一次、或没有命中过怪物(应有 casts=3 与 hits>=1):$skill_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$touch_walk_log"; then',
    'exit 1',
    'grep -q \'DNF_TOUCH on \' "$touch_walk_log" || { echo "触屏操作层没有打开:$touch_walk_log" >&2; exit 1; }',
    'grep -q \'DNF_TOWN_DEMO ran .* facing=left motion=Rest cameraX=0 leftEdge=True\' "$touch_walk_log" \\',
    '|| { echo "触屏摇杆向左奔跑后主角不在预期的状态(应停在可走区域左边界上、朝左、站立、镜头 0):$touch_walk_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$touch_skill_log"; then',
    'exit 1',
    'grep -q -E \'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 hits=[1-9][0-9]* .* casts=3 \' "$touch_skill_log" \\',
    '|| { echo "触屏放技能里 A、S、D 没有各放出一次、或没有命中过怪物(应有 casts=3 与 hits>=1):$touch_skill_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-death-$(printf \'%.2f\' "$at").png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-room-hurt-$(printf \'%.2f\' "$at").png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$hurt_log"; then',
    'exit 1',
    'grep -q -E \'DNF_TOWN_DEMO ran .* room=true monsters=[0-9]+/4 .* heroHits=[1-9][0-9]* heroHp=[0-9]+/[0-9]+\' "$hurt_log" \\',
    '|| { echo "实时挨打里主角没有被打中过(应有 heroHits>=1):$hurt_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-dungeon-$mode-$(printf \'%.2f\' "$at").png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$dungeon_log"; then',
    'exit 1',
    'grep -q -E \'DNF_TOWN_DEMO ran .* dungeon=.* entered=6 cleared=6 .* bossCleared=True\' "$dungeon_log" \\',
    '|| { echo "实时打副本没有走完(应进过 6 个房间、清空 6 个、首领清空):$dungeon_log" >&2; exit 1; }',
    'grep -q -E \'DNF_TOWN_DEMO ran .* dungeon=.* heroHits=[0-9]+ heroHp=[1-9][0-9]*/[0-9]+\' "$dungeon_log" \\',
    '|| { echo "实时打副本结束时主角应还活着(heroHp 不为 0,日志里要有 heroHits 与 heroHp):$dungeon_log" >&2; exit 1; }',
    'grep -q -E \'DNF_TOWN_DEMO ran .* monsters=[0-9]+/[0-9]+ scripted=[0-9]+ aware=[0-9]+ hits=\' "$dungeon_log" \\',
    '|| { echo "实时打副本的状态日志里应有 scripted= 与 aware= 字段:$dungeon_log" >&2; exit 1; }',
    'grep -q -E \'DNF_TOWN_DEMO ran .* settlement=True camp=[1-9][0-9]*/[0-9]+ choice=None\' "$dungeon_log" \\',
    '|| { echo "实时打副本没有进到结算(应有 settlement=True、营火至少一层、choice=None):$dungeon_log" >&2; exit 1; }',
    'grep -q -E \'DNF_AUDIO ready \' "$dungeon_log" \\',
    '|| { echo "实时打副本没有打开声音(日志里应有 DNF_AUDIO ready):$dungeon_log" >&2; exit 1; }',
    'audio_line="$(grep -E \'DNF_AUDIO cues=\' "$dungeon_log" | tail -1)"',
    'echo "$audio_line" | grep -q -E \'cues=[1-9][0-9]* .* started=[1-9][0-9]* .* loaded=[1-9][0-9]* failed=0 \' \\',
    '|| { echo "声音的计数不对(应有 cues>=1、started>=1、loaded>=1、failed=0):$dungeon_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-flow-select-$shot_ms.png" "out/town-cpu-flow-select-$cpu_sec.png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-charselect-$shot_ms.png" "out/town-cpu-charselect-$shot_ms.png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-splash-$from.png" "out/town-cpu-splash-$from-0.50.png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'python3 tools/py/png_diff.py "out/town-demo-$tag.png" "out/town-cpu-$tag.png" --max-diff 0',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$flow_log"; then',
    'exit 1',
    '[ "$(grep -c -E \'DNF_FLOW serial=\' "$flow_log")" -eq 16 ] \\',
    '|| { echo "整条流程应有十六次阶段切换(DNF_FLOW 行):$flow_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FLOW serial=1 from=[A-Za-z]+ to=CharacterSelect reason="start"\' "$flow_log" \\',
    '|| { echo "整条流程应从选角界面开始(第一行 DNF_FLOW 应是进入 CharacterSelect):$flow_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FLOW serial=2 from=CharacterSelect to=Loading reason="login"\' "$flow_log" \\',
    '|| { echo "选角之后应登录进城(第二行 DNF_FLOW 应是 CharacterSelect -> Loading,原因 login):$flow_log" >&2; exit 1; }',
    'grep -q -E \'DNF_TOWN_DEMO ran .* flow=Town serial=16 runs=3 settlements=3 botDone=True choicesMade=3\' "$flow_log" \\',
    '|| { echo "整条流程没有走完(应停在城镇、三局副本、三次菜单选择、自动机说走完了):$flow_log" >&2; exit 1; }',
    'flow_audio="$(grep -E \'DNF_AUDIO cues=\' "$flow_log" | tail -1)"',
    'echo "$flow_audio" | grep -q -E \'cues=[1-9][0-9]* .* started=[1-9][0-9]* .* loaded=[1-9][0-9]* failed=0 \' \\',
    '|| { echo "整条流程的声音计数不对(应有 cues>=1、started>=1、loaded>=1、failed=0):$flow_log" >&2; exit 1; }',
    'first_music="$(grep -E \'DNF_MUSIC id=\' "$flow_log" | head -1)"',
    'echo "$first_music" | grep -q -E \'DNF_MUSIC id=M_CHARACTER_SELECT \' \\',
    '|| { echo "第一首背景音乐应是选角音乐 M_CHARACTER_SELECT:$flow_log" >&2; exit 1; }',
    'music_lines="$(grep -c -E \'DNF_MUSIC id=\' "$flow_log")"',
    '[ "$music_lines" -ge 7 ] \\',
    '|| { echo "整条流程里背景音乐应至少开始七次(选角、城镇区域、三局副本),实际 $music_lines:$flow_log" >&2; exit 1; }',
    '[ "$(grep -c -E \'DNF_RESULT_SOUND id=M_RESULT \' "$flow_log")" -eq 3 ] \\',
    '|| { echo "三个结算界面应各放一声 M_RESULT(DNF_RESULT_SOUND 行):$flow_log" >&2; exit 1; }',
    'echo "$flow_audio" | grep -q -E "musicStarts=$music_lines resultSounds=3 " \\',
    '|| { echo "声音计数里的 musicStarts/resultSounds 与日志行数不一致:$flow_log" >&2; exit 1; }',
    'grep -E \'DNF_GOLD dropped=\' "$flow_log" | tail -1 | grep -q -E \'DNF_GOLD dropped=([1-9][0-9]*) picked=\\1 \' \\',
    '|| { echo "整条流程里自动机应捡完掉在地上的金币(最后一行 DNF_GOLD 的 dropped 与 picked 相等且不为 0):$flow_log" >&2; exit 1; }',
    'if grep -q -E \'DNF_ITEM dropped=\' "$flow_log"; then',
    'grep -E \'DNF_ITEM dropped=\' "$flow_log" | tail -1 | grep -q -E \'DNF_ITEM dropped=([0-9]+) picked=\\1 full=0 \' \\',
    '|| { echo "整条流程里自动机应捡完掉在地上的物品(最后一行 DNF_ITEM 的 dropped 与 picked 相等、full=0):$flow_log" >&2; exit 1; }',
    '[ "$(grep -c -E \'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=\' "$flow_log")" -eq 3 ] \\',
    '|| { echo "整条流程三局副本应各有一行 DNF_CLEAR:$flow_log" >&2; exit 1; }',
    '[ "$(grep -c -E \'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=[A-Z]+ frame=[0-8] ranked=True .* kills=[1-9][0-9]* .* granted=False exp=0 bonus=0 levels=0\' "$flow_log")" -eq 3 ] \\',
    '|| { echo "三行 DNF_CLEAR 都应读到评分数据(ranked=True)、打死过怪物、没有等级进度时不发经验(granted=False exp=0 bonus=0):$flow_log" >&2; exit 1; }',
    'python3 tools/py/check_clear_log.py "$flow_log" 3 \\',
    '|| { echo "DNF_CLEAR 的评分字母与帧对不上:$flow_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$fail_log"; then',
    'exit 1',
    'grep -q -E \'DNF_REVIVE deaths=1 revives=0 coins=0\' "$fail_log" \\',
    '|| { echo "没有币的主角应死一次、没有复活(DNF_REVIVE deaths=1 revives=0 coins=0):$fail_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FLOW serial=[0-9]+ from=Dungeon to=(Loading|Town) reason="dungeon fail" runs=1 settlements=0 failures=1 giveups=0\' "$fail_log" \\',
    '|| { echo "失败计时走完应回城镇、不算结算(reason=dungeon fail、settlements=0、failures=1):$fail_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$revive_log"; then',
    'exit 1',
    'grep -q -E \'DNF_REVIVE deaths=[1-9][0-9]* revives=[1-9][0-9]* coins=\' "$revive_log" \\',
    '|| { echo "币给足的主角死后应用币复活(DNF_REVIVE 行里 deaths 与 revives 都不为 0):$revive_log" >&2; exit 1; }',
    'if grep -q -E \'reason="dungeon fail"\' "$revive_log"; then',
    'exit 1',
    'grep -q -E \'DNF_GOLD dropped=[1-9][0-9]* picked=[1-9][0-9]* earned=[1-9][0-9]* purse=[1-9][0-9]*\' "$revive_log" \\',
    '|| { echo "死亡复活那一趟里主角应捡到金币、钱包里有钱(DNF_GOLD 行的 picked、earned、purse 都不为 0):$revive_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$clear_log"; then',
    'exit 1',
    'grep -q -E \'DNF_CLEAR difficulty=[0-9]+ opened=[0-9]+ rank=[A-Z]+ frame=[0-8] ranked=True .* kills=[1-9][0-9]* .* granted=True exp=[1-9][0-9]* bonus=[0-9]+ levels=[0-9]+\' "$clear_log" \\',
    '|| { echo "带等级进度打通副本应结算出评分并发了通关经验(DNF_CLEAR … ranked=True … granted=True exp>0):$clear_log" >&2; exit 1; }',
    'python3 tools/py/check_clear_log.py "$clear_log" 1 \\',
    '|| { echo "DNF_CLEAR 的评分字母与帧对不上:$clear_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$difficulty_log"; then',
    'exit 1',
    '|| { echo "三局副本的难度应依次是 0、1、1(自动机从选择界面升到记录允许的最高一档,再次挑战沿用):$difficulty_log" >&2; exit 1; }',
    'grep -q -E \'DNF_CLEAR difficulty=0 opened=1 \' "$difficulty_log" \\',
    '|| { echo "难度 0 通关应写下通关记录并开放难度 1(DNF_CLEAR difficulty=0 opened=1):$difficulty_log" >&2; exit 1; }',
    'python3 tools/py/check_clear_log.py "$difficulty_log" 3 2 \\',
    '|| { echo "难度日志里的 DNF_CLEAR 对不上:$difficulty_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$menu_log"; then',
    'exit 1',
    "for pat in 'DNF_MENU probe open items=3' 'DNF_MENU open stage=Dungeon' 'DNF_MENU probe choose 2' 'DNF_MENU closed'; do",
    'grep -q -F "$pat" "$menu_log" || { echo "系统菜单的日志里缺 \\"$pat\\":$menu_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FLOW serial=[0-9]+ from=Dungeon to=(Loading|Town) reason="menu return" runs=1 settlements=0 failures=0 giveups=1\' "$menu_log" \\',
    '|| { echo "菜单放弃回城应回城镇、算一次放弃(reason=menu return、settlements=0、failures=0、giveups=1):$menu_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$bag_log"; then',
    'exit 1',
    "for pat in 'DNF_BAG probe open granted=True' 'DNF_BAG open stage=Dungeon paused=True' 'DNF_BAG probe equip ok=True' 'DNF_BAG probe closed equipped=1' 'DNF_BAG closed paused=False' 'DNF_BAG probe look matches=True weapon=True'; do",
    'grep -q -F "$pat" "$bag_log" || { echo "背包与装备栏的日志里缺 \\"$pat\\":$bag_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FONT ready file=\' "$bag_log" \\',
    '|| { echo "流程里应当装上带汉字的系统字体(DNF_FONT ready):$bag_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]\' "$bag_log" \\',
    '|| { echo "背包窗口应当画过汉字(DNF_FONT stats glyphs>0):$bag_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$shop_log"; then',
    'exit 1',
    'grep -q -E \'DNF_SHOP town serial=[0-9]+ npcs=[0-9]+ shopnpcs=[1-9][0-9]*\' "$shop_log" \\',
    '|| { echo "NPC 商店的日志里进城时应记到摆着开商店的 NPC(DNF_SHOP town … shopnpcs>0):$shop_log" >&2; exit 1; }',
    'grep -q -E \'DNF_SHOP probe open shop=[0-9]+ items=[1-9][0-9]* tabs=\' "$shop_log" \\',
    '|| { echo "NPC 商店的探针应打开一家有商品的店(DNF_SHOP probe open … items>0):$shop_log" >&2; exit 1; }',
    'grep -q -E \'DNF_SHOP open shop=[0-9]+ items=[1-9][0-9]* paused=True\' "$shop_log" \\',
    '|| { echo "开商店时游戏应暂停(DNF_SHOP open … paused=True):$shop_log" >&2; exit 1; }',
    "for pat in 'DNF_SHOP probe trade bought=True sold=True' 'DNF_SHOP probe closed open=False' 'DNF_SHOP closed paused=False' 'DNF_SHOP probe done'; do",
    'grep -q -F "$pat" "$shop_log" || { echo "NPC 商店的日志里缺 \\"$pat\\":$shop_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]\' "$shop_log" \\',
    '|| { echo "商店窗口应当画过汉字(DNF_FONT stats glyphs>0):$shop_log" >&2; exit 1; }',
    'grep -q -E \'DNF_HUD town drawn serial=[0-9]+ level=[0-9]+ sprites=[1-9][0-9]*\' "$shop_log" \\',
    '|| { echo "有等级进度的城镇里应画过底栏(DNF_HUD town drawn … sprites>0):$shop_log" >&2; exit 1; }',
    'grep -q -E \'DNF_PLATE town serial=[0-9]+ npcs=[1-9][0-9]* hero=1\' "$shop_log" \\',
    '|| { echo "城镇里应画过主角名牌与至少一个 NPC 名字(DNF_PLATE town … npcs>0 hero=1):$shop_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed|DNF_QUEST problem\' "$quest_log"; then',
    'exit 1',
    'grep -q -E \'DNF_QUEST ready listed=[1-9][0-9]*\' "$quest_log" \\',
    '|| { echo "任务系统应在进城前准备好(DNF_QUEST ready … listed>0):$quest_log" >&2; exit 1; }',
    'grep -q -E \'DNF_QUEST town serial=[0-9]+ marks=[1-9][0-9]* available=[1-9][0-9]*\' "$quest_log" \\',
    '|| { echo "城镇里应有头顶带可接任务标记的 NPC(DNF_QUEST town … available>0):$quest_log" >&2; exit 1; }',
    'grep -q -E \'DNF_QUEST marks drawn serial=[0-9]+ count=[1-9][0-9]*\' "$quest_log" \\',
    '|| { echo "头顶标记应画过(DNF_QUEST marks drawn … count>0):$quest_log" >&2; exit 1; }',
    'grep -q -E \'DNF_QUEST probe open rows=[1-9][0-9]* \' "$quest_log" \\',
    '|| { echo "探针应打开一个有任务的 NPC 对话(DNF_QUEST probe open … rows>0):$quest_log" >&2; exit 1; }',
    'grep -q -E \'DNF_QUEST window open mode=Npc rows=[1-9][0-9]* paused=True\' "$quest_log" \\',
    '|| { echo "开任务窗口时游戏应暂停(DNF_QUEST window open mode=Npc … paused=True):$quest_log" >&2; exit 1; }',
    'grep -q -E \'DNF_QUEST probe accept acted=True result=Ok accepted=1 active=[1-9]\' "$quest_log" \\',
    '|| { echo "探针应接下一个任务(DNF_QUEST probe accept acted=True result=Ok accepted=1):$quest_log" >&2; exit 1; }',
    "for pat in 'DNF_QUEST probe closed open=False menu=False' 'DNF_QUEST probe journal opened=True mode=Journal' 'DNF_QUEST probe abandon first=False second=True result=Ok abandoned=1 active=0' 'DNF_QUEST probe closed open=False' 'DNF_QUEST window closed paused=False' 'DNF_QUEST probe done accepted=1 abandoned=1'; do",
    'grep -q -F "$pat" "$quest_log" || { echo "任务系统的日志里缺 \\"$pat\\":$quest_log" >&2; exit 1; }',
    'grep -q -E \'DNF_QUEST toast shown=[1-9][0-9]* \' "$quest_log" \\',
    '|| { echo "接任务与放弃都应出过提示条(DNF_QUEST toast shown>0):$quest_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]\' "$quest_log" \\',
    '|| { echo "任务窗口应当画过汉字(DNF_FONT stats glyphs>0):$quest_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed|DNF_SKILL probe failed\' "$skill_win_log"; then',
    'exit 1',
    'grep -q -E \'DNF_SKILL probe open page=[0-3] rows=[0-9]+ sp=[1-9][0-9]* ap=[0-9]+ learned=[1-9][0-9]* level=40\' "$skill_win_log" \\',
    '|| { echo "探针应打开技能窗口(DNF_SKILL probe open … sp>0 learned>0 level=40):$skill_win_log" >&2; exit 1; }',
    'grep -q -E \'DNF_SKILL window open page=[0-3] rows=[0-9]+ paused=True\' "$skill_win_log" \\',
    '|| { echo "开技能窗口时游戏应暂停(DNF_SKILL window open … paused=True):$skill_win_log" >&2; exit 1; }',
    'grep -q -E \'DNF_SKILL probe learn found=True pages=[0-9]+/[0-9]+/[0-9]+/[0-9]+ learned=True result=Ok \' "$skill_win_log" \\',
    '|| { echo "探针应学成一个技能(DNF_SKILL probe learn found=True … learned=True result=Ok):$skill_win_log" >&2; exit 1; }',
    'grep -q -E \'DNF_SKILL probe refund refunded=True result=Ok \' "$skill_win_log" \\',
    '|| { echo "探针应退成一级(DNF_SKILL probe refund refunded=True result=Ok):$skill_win_log" >&2; exit 1; }',
    "for pat in 'DNF_SKILL probe assign ok=True' 'DNF_SKILL probe reset first=False second=True resets=1' 'DNF_SKILL probe closed open=False' 'DNF_SKILL probe done learns=1 refunds=1 resets=1 assigns=1'; do",
    'grep -q -F "$pat" "$skill_win_log" || { echo "技能窗口的日志里缺 \\"$pat\\":$skill_win_log" >&2; exit 1; }',
    'grep -q -E \'DNF_FONT stats glyphs=[1-9][0-9]* sizes=[1-9]\' "$skill_win_log" \\',
    '|| { echo "技能窗口应当画过汉字(DNF_FONT stats glyphs>0):$skill_win_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$skill_learn_log"; then',
    'exit 1',
    'grep -q -E \'DNF_SKILL planner levels=[1-9][0-9]* sp=[1-9][0-9]*>[0-9]+ ap=[0-9]+>[0-9]+ learnedCount=[1-9][0-9]*\' "$skill_learn_log" \\',
    '|| { echo "自动机策略应花掉技能点(DNF_SKILL planner levels>0):$skill_learn_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$potion_log"; then',
    'exit 1',
    "for pat in 'DNF_POTION probe grant kinds=2 hurt=True' 'DNF_POTION probe use first=Ok again=Cooling second=Ok' 'DNF_POTION probe settled recovering=False' 'DNF_POTION probe done last='; do",
    'grep -q -F "$pat" "$potion_log" || { echo "消耗品的日志里缺 \\"$pat\\":$potion_log" >&2; exit 1; }',
    'grep -q -E \'DNF_POTION probe use first=Ok again=Cooling second=Ok .* used=2 quick=2 cooling=[12]$\' "$potion_log" \\',
    '|| { echo "连用三次后应是用成两件、快捷栏占两格、一到两种药在冷却(used=2 quick=2 cooling=1或2):$potion_log" >&2; exit 1; }',
    'grep -q -E \'DNF_POTION probe settled recovering=False .* pending=0/0 \' "$potion_log" \\',
    '|| { echo "药效应已发完(pending=0/0):$potion_log" >&2; exit 1; }',
    'grep -q -E \'DNF_LABEL drawn=[1-9][0-9]* selected=[0-9]+\' "$potion_log" \\',
    '|| { echo "副本里掉在地上的金币应画过名牌(DNF_LABEL drawn>0):$potion_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$smash_log"; then',
    'exit 1',
    'grep -q -E \'DNF_OBJECT breakables=[1-9][0-9]* hit=[1-9][0-9]* destroyed=[1-9][0-9]* itemactions=[1-9][0-9]* \' "$smash_log" \\',
    '|| { echo "自动机应当打中并打翻过物件、执行过 [item] 动作(DNF_OBJECT breakables/hit/destroyed/itemactions 都大于 0):$smash_log" >&2; exit 1; }',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$save_log1"; then',
    'exit 1',
    'grep -q -F \'DNF_SAVE open file=m26-hero.sav found=False\' "$save_log1" \\',
    '|| { echo "第一趟开始时不应有存档(DNF_SAVE open … found=False):$save_log1" >&2; exit 1; }',
    '[ -s "$save_file" ] || { echo "第一趟结束后应有存档文件:$save_file" >&2; exit 1; }',
    'grep -q -E \'^dclear [0-9]+ [1-4]$\' "$save_file" \\',
    '|| { echo "第一趟打通了副本,存档里应有通关记录行(dclear 副本编号 值):$save_file" >&2; exit 1; }',
    'saved_fields="$(grep -E \'DNF_SAVE saved\' "$save_log1" | tail -1 | grep -o -E \'level=[0-9]+ exp=[0-9]+ gold=[0-9]+ bag=[0-9]+ equipped=[0-9]+\' || true)"',
    'case "$saved_fields" in',
    '*) echo "第一趟最后存下的应穿着 1 件装备(DNF_SAVE saved … equipped=1),实际:$saved_fields" >&2; exit 1 ;;',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'if grep -q -E \'Exception|DNF_TOWN_DEMO failed\' "$save_log2"; then',
    'exit 1',
    'loaded_fields="$(grep -E \'DNF_SAVE loaded\' "$save_log2" | tail -1 | grep -o -E \'level=[0-9]+ exp=[0-9]+ gold=[0-9]+ bag=[0-9]+ equipped=[0-9]+\' || true)"',
    '[ -n "$loaded_fields" ] && [ "$loaded_fields" = "$saved_fields" ] \\',
    '|| { echo "第二趟读到的应与第一趟最后存下的一样:存下 \\"$saved_fields\\"、读到 \\"$loaded_fields\\":$save_log2" >&2; exit 1; }',
    'grep -q -E \'DNF_SAVE loaded .* dropped=0\' "$save_log2" || { echo "读存档时不应丢东西(dropped=0):$save_log2" >&2; exit 1; }',
    'grep -q -F \'DNF_SAVE look matches=True weapon=True\' "$save_log2" \\',
    '|| { echo "读了存档进城镇,主角应已换上穿着的武器(DNF_SAVE look matches=True weapon=True):$save_log2" >&2; exit 1; }',
    'if grep -q -E \'DNF_SAVE saved\' "$save_log2"; then',
    'exit 1',
    '[ "$rc" -eq 0 ] || exit "$rc"',
    'grep -q -F \'DNF_TOWN_DEMO online source=None configured=False dev=True serverui=True\' "$dev_log" \\',
    '|| { echo "开发版未报 dev=True / source=None / configured=False / serverui=True:$dev_log" >&2; exit 1; }',
)


def is_criteria_line(line: str) -> bool:
    t = line.strip()
    if not t or t.startswith("#"):
        return False
    if t.startswith("for pat in "):
        return True
    if t.startswith("echo ") and "grep" not in t and "||" not in t:
        return False
    if (t.startswith("grep -E ") or t.startswith("grep -n ")) and t.endswith("|| true"):
        return False
    if t.startswith("grep -n ") and "DNF_BUILD" in t:
        return False
    if t.startswith("grep -n -E ") and "BuildFailedException" in t:
        return False
    return any(k in t for k in (
        "png_diff.py", "check_clear_log.py", "grep -q", "grep -c",
        "\"$rc\" -eq 0", "cli_hero", "saved_fields", "loaded_fields",
        "music_lines", "first_music", "flow_audio", "audio_line",
        "[ -s \"$save_file\" ]", "case \"$cli_hero\"", "case \"$saved_fields\"",
        "DNF_TOWN_DEMO failed", "DNF_QUEST problem", "DNF_SKILL probe failed",
        "reason=\"dungeon fail\"", "DNF_SAVE saved", "|| { echo ", "exit 1"
    ))


def join_logical_lines(lines):
    logical = []
    buf = []
    start_ln = 1
    for ln, raw in enumerate(lines, start=1):
        stripped = raw.rstrip()
        if not buf:
            start_ln = ln
        if stripped.endswith("\\") and not stripped.lstrip().startswith("#"):
            buf.append(stripped[:-1].strip())
        else:
            buf.append(stripped.strip())
            logical.append((start_ln, " ".join(part for part in buf if part)))
            buf = []
    if buf:
        logical.append((start_ln, " ".join(part for part in buf if part)))
    return logical


# 规则 (a) 的词法:一条正则一次扫描逻辑行,四类记号按下面的顺序试,先中先得(都与 bash 实测一致):
#   comment  词首(行首、空白或分隔符之后)的 # 起到行尾;单词中间的 # 已被下面的"词"整个吃掉,轮不到这里
#   word     重定向记号(> >> < << >& <& &> &>> >|)自成一个词、不切段,所以 2>&1 里的 & 不会把调用切开;
#            其余由 反斜杠转义 / 单引号串 / 双引号串(里面只有反斜杠转义) / 普通字符 连成一个词
#   sep      命令分隔符 ; & | ( ) 和反引号(&& 与 || 是相邻的两个分隔符,中间夹一个空段)
#   空白     只有空格与制表符(与 shell 一致:NBSP、全角空格是词的一部分),一个字符一个记号
# 引号不成对、行尾孤立的反斜杠:没有任何记号吃得下它们,由 _shell_segments 报 _Unsplittable。
_SHELL_TOKEN = re.compile(r'''
    (?:\#.*)
  | (?P<word>&>>?|[<>]+[&|]?|(?:\\.|'[^']*'|"(?:\\.|[^"\\])*"|[^ \t;&|()`<>'"\\])+)
  | (?P<sep>[;&|()`])
  | [ \t]
''', re.X)

# -dnfquitaftersim 的值:ASCII 数字、至多一个小数点(点两侧都要有数字),且至少一位非零数字(即严格正数)。
# 前瞻:跳过开头的 0 与小数点,后面还得有别的字符(整体已是数字格式,所以那就是 1-9)。
# 保守:.5、5.、1e3、+5、阿拉伯-印度数字一律不收(与上一版口径一致)。
_POSITIVE_NUMBER = re.compile(r"(?=[0.]*[^0.])[0-9]+(?:\.[0-9]+)?")


class _Unsplittable(Exception):
    """逻辑行无法按 shell 语法拆成词:引号不成对,或行尾有孤立的反斜杠。"""


def _shell_segments(line):
    """按 shell 语法把一条逻辑行拆成命令段:返回 [[词, ...], ...],每个词已去掉引号与转义。
    注释截掉;引号外的 ; & | ( ) 与反引号切段,相邻的操作符之间留一个空段。
    引号不成对或行尾有孤立的反斜杠:抛 _Unsplittable(下面的 shlex 只处理已成对的词,不会抛 ValueError)。"""
    import shlex  # 放在函数里:文件头一行都不动,按"文件:行号"定位的既有变异不会错位(与 _cluster 里的 traceback 同一做法)

    tokens = list(_SHELL_TOKEN.finditer(line))
    if "".join(m.group() for m in tokens) != line:
        raise _Unsplittable  # 有字符没被任何记号吃掉
    segments = [[]]
    for m in tokens:
        if m.lastgroup == "word":
            (word,) = shlex.split(m.group())  # 一个词里引号成对、没有引号外的空白:恰好得到一个词
            segments[-1].append(word)
        elif m.lastgroup == "sep":
            segments.append([])
    return segments


def _has_positive_quitaftersim(words):
    """同一命令段里是否有连续的两个词:-dnfquitaftersim、严格正数。
    出现多次时只要有一对合格就行:宿主逐个解析,值不是正数的会被忽略,较晚的合格值生效。"""
    return any(
        flag == "-dnfquitaftersim" and _POSITIVE_NUMBER.fullmatch(value) is not None
        for flag, value in zip(words, words[1:])
    )


def check_text(text: str, label: str = "tools/unity-build-town.sh"):
    errors = []
    lines = text.splitlines()

    # (a) 每一个 $app 调用(同一命令段里有词 $app 或 ${app})只要带 -dnftimescale,
    #     同一段里就必须有连续的两个词 -dnfquitaftersim、严格正数。按 shell 语法拆词:注释与别的命令段里的参数不算数
    timescale_count = 0
    for start_ln, cmd in join_logical_lines(lines):
        if "-dnftimescale" not in cmd:  # 不含该子串的行一律不拆词:别的行里的任意引号写法不会误伤
            continue
        try:
            segments = _shell_segments(cmd)
        except _Unsplittable:
            errors.append(f"{label}:{start_ln}: 含 -dnftimescale 的逻辑行无法按 shell 语法拆分(引号不成对?)")
            continue
        for words in segments:
            if ("$app" in words or "${app}" in words) and "-dnftimescale" in words:
                timescale_count += 1
                if not _has_positive_quitaftersim(words):
                    errors.append(f"{label}:{start_ln}: 带 -dnftimescale 的 $app 调用缺少正数 -dnfquitaftersim 参数")

    if timescale_count == 0:
        errors.append(f"{label}: 未找到任何带 -dnftimescale 的 $app 调用")

    # (b) Pass criteria lines (from the verification section onward) must match baseline verbatim
    verify_started = False
    actual_list = []
    for l in lines:
        if not verify_started:
            if l.strip().startswith('echo "已构建:'):
                verify_started = True
            continue
        if is_criteria_line(l):
            actual_list.append(l.strip())
    actual_criteria = tuple(actual_list)
    if actual_criteria != BASELINE_CRITERIA_LINES:
        min_len = min(len(actual_criteria), len(BASELINE_CRITERIA_LINES))
        diff_idx = next((i for i in range(min_len) if actual_criteria[i] != BASELINE_CRITERIA_LINES[i]), min_len)
        exp = BASELINE_CRITERIA_LINES[diff_idx] if diff_idx < len(BASELINE_CRITERIA_LINES) else "<EOF>"
        act = actual_criteria[diff_idx] if diff_idx < len(actual_criteria) else "<EOF>"
        errors.append(
            f"{label}: 通过判据行与基线不一致 (基线 {len(BASELINE_CRITERIA_LINES)} 行, 实际 {len(actual_criteria)} 行, 首个差异下标 {diff_idx}):\n"
            f"  期望: {exp}\n"
            f"  实际: {act}"
        )

    return errors, timescale_count, len(actual_criteria)


# ---------------------------------------------------------------------------
# 细粒度自测对照:钉住 check_text / is_criteria_line / join_logical_lines 的边界与分支。
# 约定:期望值一律手写字面量,不拿被测函数"算期望";错误信息逐字断言整行;
#      合成文本总会多一条"通过判据行与基线不一致",所以按错误类别筛选,不用"错误列表为空"判断。
# ---------------------------------------------------------------------------

# 真实脚本的规模(手写钉住):刻意增删带 -dnftimescale 的调用、或更新 BASELINE_CRITERIA_LINES 时,同步改这两个数。
_PIN_CALLS = 15
_PIN_CRITERIA = 262

_LABEL = "<t>"
_CAT_SIM = "缺少正数"
_CAT_NO_CALL = "未找到任何"
_CAT_NOSPLIT = "无法按 shell 语法拆分"  # 信息是"…逻辑行无法按 shell 语法拆分(引号不成对?)",并不含连续的"无法拆分",所以按这段连续的片段筛选
_FIRST_CRITERION = '[ "$rc" -eq 0 ] || exit "$rc"'  # 基线第 0 条:验证段一条判据都没有时,"期望:"一行就是它

# is_criteria_line:不该计入判据的行(注释、空行、说明性 echo、信息性 grep)
_NOT_CRITERIA = (
    "",
    "   ",
    "# grep -q 说明 || exit 1",
    "    # exit 1",
    'echo "说明 exit 1"',
    '  echo "说明 exit 1"  ',
    "grep -E 'DNF_SAVE saved' \"$log\" || true",
    "grep -n 'DNF_SAVE saved' \"$log\" || true",
    "grep -n -E 'DNF_SAVE saved' \"$log\" || true",
    "  grep -E 'DNF_SAVE saved' \"$log\" || true  ",
    "grep -n 'DNF_BUILD ok' \"$log\" | grep -c ok",
    "  grep -n 'DNF_BUILD ok' \"$log\" | grep -c ok  ",
    "grep -n -E 'BuildFailedException' \"$log\" | grep -c Build",
    "x for pat in a b",
    "for pat inx a b",
    'echo "rep exit 1"',
    'echo "gre exit 1"',
    'echo "说明 | exit 1"',
    "foo bar",
)
# is_criteria_line:必须计入判据的行——含关键字、却长得像上面某条排除规则的行(排除规则不能过宽)
_ARE_CRITERIA = (
    "for pat in a b; do",
    "  for pat in x y",
    "grep -q 'x' \"$log\" || exit 1",
    "grep -q 'a#b' \"$log\" || exit 1",
    "grep -q 'x' \"$log\" || exit 1 #",
    "grep -q 'DNF_BUILD ok' \"$log\" || exit 1",
    "grep -q 'BuildFailedException' \"$log\" || exit 1",
    "grep -q 'x' \"$log\" || true",
    "grep -E 'DNF_SAVE saved' \"$log\"",
    "grep -n 'DNF_SAVE saved' \"$log\"",
    "grep -n -E 'DNF_SAVE saved' \"$log\"",
    "grep -E 'DNF_SAVE saved' \"$log\" || true; exit 1",
    "xgrep -E 'DNF_SAVE saved' \"$log\" || true",
    "xgrep -n 'DNF_SAVE saved' \"$log\" || true",
    "xgrep -n 'DNF_BUILD ok' \"$log\" || exit 1",
    "xgrep -n -E 'BuildFailedException' \"$log\" || exit 1",
    'echo "x" || exit 1',
    'echo "grep -c ok"',
    'x echo "说明 exit 1"',
    "echoed exit 1",
    "grep -E 'DNF_SAVE saved' \"$log\" | true",
    "grep -Ex 'DNF_SAVE saved' \"$log\" || true",
    "grep -nx 'DNF_SAVE saved' \"$log\" || true",
    "grep -nE 'DNF_BUILD ok' \"$log\" | grep -c ok",
    "grep -n 'NF_BUILD ok' \"$log\" | grep -c ok",
    "grep -n 'DNF_BUILX ok' \"$log\" | grep -c ok",
    "grep -n -Ex 'BuildFailedException' \"$log\" | grep -c Build",
    "grep -n -E 'uildFailedException' \"$log\" | grep -c Build",
    "grep -n -E 'BuildFailedExceptio' \"$log\" | grep -c Build",
)
# is_criteria_line 末尾那张关键字表,逐个钉住:含其中任何一个的行就是判据行。
# 其中 case "$cli_hero" 与 case "$saved_fields" 是冗余项(含它们的行必然也含更短的 cli_hero / saved_fields):
# 删掉这两项、或改成仍含那个短关键字的串,在任何输入下输出都相同;但把里面的短关键字拼坏(少一个字符、换一个字符、换大小写)
# 就成了另一个独立的关键字,含它的行会从"不是判据行"变成"是",所以簇 5 用拼坏了的近似行钉住它们的拼写;其余每一项都有各自独立的对照。
_CRITERIA_KEYWORDS = (
    "png_diff.py", "check_clear_log.py", "grep -q", "grep -c", '"$rc" -eq 0',
    "cli_hero", "saved_fields", "loaded_fields", "music_lines", "first_music",
    "flow_audio", "audio_line", '[ -s "$save_file" ]', 'case "$cli_hero"', 'case "$saved_fields"',
    "DNF_TOWN_DEMO failed", "DNF_QUEST problem", "DNF_SKILL probe failed",
    'reason="dungeon fail"', "DNF_SAVE saved", "|| { echo ", "exit 1",
)
# join_logical_lines:(名字, 输入物理行, 期望的 [(起始物理行号, 逻辑行)])
_JOIN_CASES = (
    ("续行拼成一条,起始行号是第一个物理行", ["a \\", "b \\", "c", "d"], [(1, "a b c"), (4, "d")]),
    ("反斜杠紧贴字符,各段用一个空格拼", ["a\\", "b"], [(1, "a b")]),
    ("无续行的行各自成一条", ["x", "y"], [(1, "x"), (2, "y")]),
    ("注释行末尾的反斜杠不续行", ["# 注释 \\", "x"], [(1, "# 注释 \\"), (2, "x")]),
    ("缩进的注释行末尾的反斜杠也不续行", ["  # 缩进注释 \\", "x"], [(1, "# 缩进注释 \\"), (2, "x")]),
    ("末行以反斜杠结尾:仍返回已拼的一条", ["a \\", "b \\"], [(1, "a b")]),
    ("只有一行且以反斜杠结尾", ["a \\"], [(1, "a")]),
    ("末行只有反斜杠:空段不留多余空格", ["a \\", " \\"], [(1, "a")]),
    ("空输入", [], []),
    ("空行结束续行,空段不留多余空格", ["a \\", "", "c"], [(1, "a"), (3, "c")]),
    ("只有反斜杠的续行行不留多余空格", ["a \\", " \\", "b"], [(1, "a b")]),
    ("空行各自成一条", ["", "x", ""], [(1, ""), (2, "x"), (3, "")]),
    ("反斜杠后的行尾空白仍算续行", ["a \\  ", "b"], [(1, "a b")]),
    ("每段首尾空白去掉", ["  a \\", "    b  "], [(1, "a b")]),
    ("行中间的反斜杠不是续行", ["a \\ b", "c"], [(1, "a \\ b"), (2, "c")]),
    ("引号里的 # 不是注释,行尾反斜杠照常续行", ["grep -q 'a#b' \"$log\" \\", "|| exit 1"], [(1, "grep -q 'a#b' \"$log\" || exit 1")]),
    ("多组逻辑行的起始行号", ["p", "a \\", "b", "q \\", "r \\", "s"], [(1, "p"), (2, "a b"), (4, "q r s")]),
)


class _Recorder:
    """收集细粒度对照的结果:每条对照有名字;失败时记下名字、期望与实际,最后统一报告。"""

    def __init__(self):
        self.checks = 0
        self.failures = []
        self.last = "(尚无)"  # 最近一条已完成的对照名:某簇中途抛异常时,用它大致定位
        self.clusters = []  # [(簇函数名, 这一簇跑了几条对照)]:run_selftest 末尾据此发现"有簇没被调用、或一条对照都没跑"

    def eq(self, name, actual, expected):
        self.checks += 1
        self.last = name
        if actual != expected:
            self.failures.append((name, expected, actual))


def _cluster(rec, name, fn, *args):
    """运行一簇对照;被测函数抛异常(缺陷或变异)时记为该簇的一条失败,不让回溯盖住其余对照。"""
    import traceback  # 只在失败路径用;放在这里,不让文件头多出一行而顺移被测函数的行号

    before = rec.checks
    try:
        fn(rec, *args)
    except Exception as exc:
        where = traceback.extract_tb(exc.__traceback__)[-1]
        rec.checks += 1
        rec.failures.append((
            f"{name} 运行中抛出异常(上一条已完成的对照:{rec.last})",
            "不抛异常",
            f"{type(exc).__name__}: {exc} @ {os.path.basename(where.filename)}:{where.lineno} {where.name}",
        ))
    rec.clusters.append((fn.__name__, rec.checks - before))


def _cluster_gaps(ran, expected):
    """应跑的簇函数(expected)与实际跑过的(ran:[(函数名, 对照条数)])必须恰是同一批,且每个都至少有一条对照;返回对不上的名字(已排序):
    没被调用的、跑了却一条对照都没有的,以及跑了却不在 expected 里的(发现 expected 的表达式失灵、变空或变少时,会整批冒出来)。"""
    done = {name for name, count in ran if count > 0}
    return sorted((set(expected) - done) | ({name for name, _ in ran} - set(expected)))


def _only(errors, category):
    """按错误类别(信息里的关键词)筛选。"""
    return [e for e in errors if category in e]


def _msg_sim(line_no):
    return f"{_LABEL}:{line_no}: 带 -dnftimescale 的 $app 调用缺少正数 -dnfquitaftersim 参数"


def _msg_no_call():
    return f"{_LABEL}: 未找到任何带 -dnftimescale 的 $app 调用"


def _msg_nosplit(line_no):
    return f"{_LABEL}:{line_no}: 含 -dnftimescale 的逻辑行无法按 shell 语法拆分(引号不成对?)"


def _msg_crit(actual_n, diff_idx, expected_line, actual_line):
    return (
        f"{_LABEL}: 通过判据行与基线不一致 (基线 {_PIN_CRITERIA} 行, 实际 {actual_n} 行, 首个差异下标 {diff_idx}):\n"
        f"  期望: {expected_line}\n"
        f"  实际: {actual_line}"
    )


def _sim_text(value):
    """最小合成文本:一个带 -dnftimescale 的 "$app" 调用;value 是 -dnfquitaftersim 后的值,None 表示整个参数省略。"""
    tail = "" if value is None else " -dnfquitaftersim " + value
    return '"$app" -dnftimescale 4' + tail + " -batchmode"


def _join_rows(rows):
    return "\n".join(rows) + "\n"


_CALL = '"$app" -dnftimescale 4'  # 带 -dnftimescale、没有 -dnfquitaftersim 的调用(坏的)
_GOOD = _CALL + " -dnfquitaftersim 5"  # 带正数 -dnfquitaftersim 的调用(好的)


def _verdict(text):
    """合成文本的 (缺少正数的错误, 调用数);别的类别(判据行不一致等)不看。"""
    errs, calls, _ = check_text(text, _LABEL)
    return _only(errs, _CAT_SIM), calls


def _marker_row(rows):
    """验证段起点(echo "已构建: 行)的物理下标;自测里自己找,不借用被测代码。"""
    return next(i for i, row in enumerate(rows) if row.strip().startswith('echo "已构建:'))


def _call_spans(rows):
    """真实脚本里带 -dnftimescale 的 "$app" 调用各占的物理行区间 [(首行下标, 末行下标)](0 起算)。
    自己按"行尾反斜杠 = 续行(注释行除外)"最朴素地扫一遍,不借用被测的 join_logical_lines。"""
    spans = []
    i = 0
    while i < len(rows):
        j = i
        while j + 1 < len(rows) and rows[j].rstrip().endswith("\\") and not rows[j].lstrip().startswith("#"):
            j += 1
        joined = " ".join(row.strip() for row in rows[i:j + 1])
        if not joined.startswith("#") and '"$app"' in joined and "-dnftimescale" in joined.split():
            spans.append((i, j))
        i = j + 1
    return spans


def _selftest_positive_sim(rec):
    """簇 1:-dnfquitaftersim 的值必须是正数——0、0.0、00、负数、非数字、省略都要报错,正数一律放行。"""
    one = [_msg_sim(1)]
    for value in ("0", "0.0", "00", "0.00", "abc", "-1", "-0.5", "5x", "5x3", ".5", "5.", "5.5.5", "1e3", "\u0665"):
        rec.eq(f"簇1 值 {value} 必须报缺少正数", _only(check_text(_sim_text(value), _LABEL)[0], _CAT_SIM), one)
    rec.eq("簇1 省略必须报缺少正数", _only(check_text(_sim_text(None), _LABEL)[0], _CAT_SIM), one)
    for value in ("0.05", "0.1", "0.5", "1", "8", "120.5"):
        rec.eq(f"簇1 值 {value} 不报缺少正数", _only(check_text(_sim_text(value), _LABEL)[0], _CAT_SIM), [])
    layouts = (
        ("值在逻辑行末尾", '"$app" -batchmode -dnftimescale 4 -dnfquitaftersim 8', []),
        ("参数名与值之间多个空格", '"$app" -dnftimescale 4 -dnfquitaftersim    8 -batchmode', []),
        ("参数名与值之间是制表符", '"$app" -dnftimescale 4 -dnfquitaftersim\t8 -batchmode', []),
        ("参数在逻辑行行首", '-dnfquitaftersim 8 "$app" -dnftimescale 4', []),
        ("只有墙钟 -dnfquitafter 不算", '"$app" -dnftimescale 4 -dnfquitafter 8 -batchmode', one),
        ("参数名与前一个记号粘连", '"$app" -dnftimescale 4 x-dnfquitaftersim 8 -batchmode', one),
        ("参数名与值粘连", '"$app" -dnftimescale 4 -dnfquitaftersim8 -batchmode', one),
        ("参数名后没有值(行尾)", '"$app" -batchmode -dnftimescale 4 -dnfquitaftersim', one),
        ("参数名后紧跟另一个参数", '"$app" -dnftimescale 4 -dnfquitaftersim -batchmode', one),
        ("参数名只是更长参数名的前缀", '"$app" -dnftimescale 4 -dnfquitaftersimx 8 -batchmode', one),
    )
    for name, text, want in layouts:
        rec.eq("簇1 " + name, _only(check_text(text, _LABEL)[0], _CAT_SIM), want)
    rows = ["# 头部注释", _sim_text("7"), "echo 中间", _sim_text("0"), _sim_text("6"), _sim_text(None)]
    errs, calls, _ = check_text(_join_rows(rows), _LABEL)
    rec.eq("簇1 多个调用:坏的逐个报、行号对得上、调用数照数", (_only(errs, _CAT_SIM), calls), ([_msg_sim(4), _msg_sim(6)], 4))


def _selftest_no_call(rec, valid_text):
    """簇 2:一个带 -dnftimescale 的调用都没有时必须报错;恰好 1、2、15 个合法调用时没有这条错。"""
    stripped, replaced = re.subn(r"\s-dnftimescale\s+\S+", "", valid_text)
    rec.eq("簇2 前置:真实脚本里删掉的 -dnftimescale 处数", replaced, _PIN_CALLS)
    rec.eq("簇2 真实脚本删光 -dnftimescale", check_text(stripped, _LABEL), ([_msg_no_call()], 0, _PIN_CRITERIA))
    no_criteria = _msg_crit(0, 0, _FIRST_CRITERION, "<EOF>")
    rec.eq("簇2 空文本", check_text("", _LABEL), ([_msg_no_call(), no_criteria], 0, 0))
    rec.eq(
        "簇2 只有注释的文本",
        check_text('# "$app" -dnftimescale 4 -dnfquitaftersim 5\n# 另一行注释\n', _LABEL),
        ([_msg_no_call(), no_criteria], 0, 0),
    )
    rec.eq(
        "簇2 默认标签是脚本路径",
        check_text("")[0][0],
        "tools/unity-build-town.sh: 未找到任何带 -dnftimescale 的 $app 调用",
    )
    for n in (1, 2, 15):
        text = _join_rows([_sim_text(str(i)) for i in range(1, n + 1)])
        errs, calls, _ = check_text(text, _LABEL)
        rec.eq(f"簇2 恰有 {n} 个合法调用", (_only(errs, _CAT_NO_CALL), _only(errs, _CAT_SIM), calls), ([], [], n))


def _selftest_not_a_call(rec):
    """簇 3:注释行与不含 "$app" 的行里的 -dnftimescale 不算调用——既不报缺少正数,也不计入调用数。"""
    ok = _sim_text("5")
    cases = (
        ("被注释掉的 $app 调用", '# "$app" -dnftimescale 5\n' + ok),
        ("缩进的注释", '    # "$app" -dnftimescale 5\n' + ok),
        ("被注释掉的多行调用", '# "$app" -dnftimescale 5 \\\n#   -batchmode\n' + ok),
        ("echo 文字里的记号与注释里的记号", "echo 用法 -dnftimescale 5\n# -dnftimescale 5\n" + ok),
        ("不是 $app 的命令里的记号", "dotnet run -dnftimescale 5\n" + ok),
        ("记号只是更长参数名的前缀", '"$app" -dnftimescalefoo 5\n' + ok),
    )
    for name, text in cases:
        errs, calls, _ = check_text(text, _LABEL)
        rec.eq(f"簇3 {name}不算调用", (_only(errs, _CAT_SIM), calls), ([], 1))
    # 有意的改动:引号不成对、又含 -dnftimescale 的行,以前"不算调用"悄悄放过,现在整行报"无法拆分"(失败要响,详见簇14);
    # 其余行照常检查,调用数仍是后面那条合法行的 1,也不会多出缺少正数的错
    broken = (
        ("$app 只有右引号", 'run $app" -dnftimescale 5\n' + ok),
        ("$app 只有左引号", 'run "$app -dnftimescale 5\n' + ok),
    )
    for name, text in broken:
        errs, calls, _ = check_text(text, _LABEL)
        want = ([], [_msg_nosplit(1)], 1)
        rec.eq(f"簇3 {name}:不算调用,整行报无法拆分", (_only(errs, _CAT_SIM), _only(errs, _CAT_NOSPLIT), calls), want)
    only_fake = (
        ("唯一的调用被注释掉", '# "$app" -dnftimescale 5 -dnfquitaftersim 5\n'),
        ("唯一的记号在 echo 文字里", "echo 用法 -dnftimescale 5\n"),
    )
    for name, text in only_fake:
        errs, calls, _ = check_text(text, _LABEL)
        rec.eq(f"簇3 {name}:调用数 0 且报未找到任何", (_only(errs, _CAT_NO_CALL), _only(errs, _CAT_SIM), calls), ([_msg_no_call()], [], 0))
    # 反过来:调用行末尾带一段注释,它仍是调用——带正数参数不报错,缺参数照报(注释不是豁免)
    trailing = (
        ("带正数参数", '"$app" -batchmode -dnftimescale 4 -dnfquitaftersim 5 # 说明\n', ([], 1)),
        ("缺参数", '"$app" -batchmode -dnftimescale 4 # 说明\n', ([_msg_sim(1)], 1)),
    )
    for name, text, want in trailing:
        errs, calls, _ = check_text(text, _LABEL)
        rec.eq(f"簇3 行末带注释的调用仍是调用:{name}", (_only(errs, _CAT_SIM), calls), want)


def _selftest_every_call(rec, valid_text):
    """簇 4:真实脚本里的每一个调用都被检查——逐个删掉 / 改成 0,恰有一条错,行号是该调用的起始物理行(1 起算)。"""
    rows = valid_text.splitlines()
    spans = _call_spans(rows)
    rec.eq("簇4 真实脚本里的调用数", len(spans), _PIN_CALLS)
    all_dropped = list(rows)
    expected_all = []
    for n, (first, last) in enumerate(spans, start=1):
        sim_rows = [r for r in range(first, last + 1) if "-dnfquitaftersim" in rows[r]]
        rec.eq(f"簇4 第 {n} 个调用恰有一处 -dnfquitaftersim", len(sim_rows), 1)
        if len(sim_rows) != 1:
            continue
        r = sim_rows[0]
        dropped = list(rows)
        dropped[r] = re.sub(r"\s*-dnfquitaftersim\s+\S+", "", rows[r], count=1)
        zeroed = list(rows)
        zeroed[r] = re.sub(r"(-dnfquitaftersim\s+)\S+", r"\g<1>0", rows[r], count=1)
        want = ([_msg_sim(first + 1)], _PIN_CALLS, _PIN_CRITERIA)
        rec.eq(f"簇4 只删第 {n} 个调用的 -dnfquitaftersim", check_text(_join_rows(dropped), _LABEL), want)
        rec.eq(f"簇4 把第 {n} 个调用的值改成 0", check_text(_join_rows(zeroed), _LABEL), want)
        all_dropped[r] = dropped[r]
        expected_all.append(_msg_sim(first + 1))
    rec.eq(
        "簇4 全部调用同时缺失:逐个报错、顺序与出现顺序一致",
        check_text(_join_rows(all_dropped), _LABEL),
        (expected_all, _PIN_CALLS, _PIN_CRITERIA),
    )


def _selftest_criteria_rules(rec, valid_text):
    """簇 5:判据行的抽取规则(is_criteria_line)——排除规则不漏,也不过宽。"""
    for line in _NOT_CRITERIA:
        rec.eq(f"簇5 不是判据行 {line!r}", is_criteria_line(line), False)
    for line in _ARE_CRITERIA:
        rec.eq(f"簇5 是判据行 {line!r}", is_criteria_line(line), True)
    for key in _CRITERIA_KEYWORDS:
        rec.eq(f"簇5 关键字 {key!r}", is_criteria_line("foo " + key + "bar"), True)
        if key in ('case "$cli_hero"', 'case "$saved_fields"'):
            continue  # 冗余项:它的近似行必然还含着更短的那个关键字
        # 近似行:只含关键字的一部分(少首字符 / 末字符被换掉),不是判据行
        rec.eq(f"簇5 少了首字符的 {key!r} 不算", is_criteria_line("foo " + key[1:] + "bar"), False)
        rec.eq(f"簇5 末字符不对的 {key!r} 不算", is_criteria_line("foo " + key[:-1] + "Xbar"), False)
    # 冗余项的近似行仍含着更短的关键字,所以上面跳过;但把里面那个短关键字拼坏(删掉其中任一个字符、换成 X、整体换大小写)之后,
    # 近似行不含任何关键字,不是判据行——钉住这两项的拼写
    for key, short in (('case "$cli_hero"', "cli_hero"), ('case "$saved_fields"', "saved_fields")):
        start = key.index(short)
        for i in range(start, start + len(short)):
            n = i - start + 1
            rec.eq(f"簇5 {key!r} 里短关键字少第 {n} 个字符的近似行不算", is_criteria_line("foo " + key[:i] + key[i + 1:] + "bar"), False)
            rec.eq(f"簇5 {key!r} 里短关键字第 {n} 个字符换成 X 的近似行不算", is_criteria_line("foo " + key[:i] + "X" + key[i + 1:] + "bar"), False)
        rec.eq(f"簇5 {key!r} 整体换大小写的近似行不算", is_criteria_line("foo " + key.swapcase() + "bar"), False)
    # 经 check_text:往验证段插一行说明性/信息性的行,判据行数仍是基线的数,不报错
    rows = valid_text.splitlines()
    at = _marker_row(rows) + 1
    for line in ("# grep -q 说明 || exit 1", "grep -E 'DNF_SAVE saved' \"$log\" || true",
                 "grep -n 'DNF_SAVE saved' \"$log\" || true", "grep -n 'DNF_BUILD ok' \"$log\" | grep -c ok",
                 "grep -n -E 'BuildFailedException' \"$log\" | grep -c Build", 'echo "说明 exit 1"', ""):
        rec.eq(f"簇5 验证段插入 {line!r} 不影响结果", check_text(_join_rows(rows[:at] + [line] + rows[at:]), _LABEL), ([], _PIN_CALLS, _PIN_CRITERIA))
    # 反向:真正的新判据(含 DNF_BUILD / BuildFailedException 但不是被排除的写法)必须被当成判据行而报不一致
    for line in ("grep -q 'x' \"$log\" || exit 1", "grep -q 'DNF_BUILD ok' \"$log\" || exit 1",
                 "grep -q 'BuildFailedException' \"$log\" || exit 1"):
        rec.eq(
            f"簇5 末尾追加真判据 {line!r} 必须报不一致",
            check_text(valid_text + line + "\n", _LABEL),
            ([_msg_crit(_PIN_CRITERIA + 1, _PIN_CRITERIA, "<EOF>", line)], _PIN_CALLS, _PIN_CRITERIA + 1),
        )


def _selftest_join_lines(rec):
    """簇 6:逻辑行拼接(join_logical_lines)——续行、注释、行号、空段、空输入。"""
    for name, rows, want in _JOIN_CASES:
        rec.eq(f"簇6 {name}", join_logical_lines(rows), want)
    text = _join_rows(["echo 头", '"$app" -batchmode \\', "  -dnfdir x \\", "  -dnftimescale 4 -dnfquitafter 9", "echo 尾"])
    errs, calls, _ = check_text(text, _LABEL)
    rec.eq("簇6 多行调用缺少参数:错误行号是调用的第一个物理行", (_only(errs, _CAT_SIM), calls), ([_msg_sim(2)], 1))


def _selftest_result_contents(rec, valid_text):
    """簇 7:返回值与错误信息的内容——调用数、判据行数、首个差异下标、期望与实际两行逐字。"""
    rec.eq("簇7 未篡改文本的返回值", check_text(valid_text, _LABEL), ([], _PIN_CALLS, _PIN_CRITERIA))
    rows = valid_text.splitlines()
    crit_rows = [i for i in range(_marker_row(rows) + 1, len(rows)) if is_criteria_line(rows[i])]
    rec.eq("簇7 验证段判据行数", len(crit_rows), _PIN_CRITERIA)
    for k in (0, _PIN_CRITERIA // 2, _PIN_CRITERIA - 1):
        original = rows[crit_rows[k]].strip()
        altered = original + " # 篡改"
        edited = list(rows)
        edited[crit_rows[k]] = altered
        rec.eq(
            f"簇7 篡改第 {k} 条判据",
            check_text(_join_rows(edited), _LABEL),
            ([_msg_crit(_PIN_CRITERIA, k, original, altered)], _PIN_CALLS, _PIN_CRITERIA),
        )
    # 删掉最后一条:差异出现在基线末尾,"实际:"是 <EOF>
    last = rows[crit_rows[-1]].strip()
    edited = list(rows)
    edited[crit_rows[-1]] = ""
    rec.eq(
        "簇7 删除最后一条判据",
        check_text(_join_rows(edited), _LABEL),
        ([_msg_crit(_PIN_CRITERIA - 1, _PIN_CRITERIA - 1, last, "<EOF>")], _PIN_CALLS, _PIN_CRITERIA - 1),
    )
    # 删掉第一条:后面的整体前移,差异在下标 0,"期望:"与"实际:"是相邻两条真实判据
    first = rows[crit_rows[0]].strip()
    second = rows[crit_rows[1]].strip()
    edited = list(rows)
    edited[crit_rows[0]] = ""
    rec.eq(
        "簇7 删除第一条判据",
        check_text(_join_rows(edited), _LABEL),
        ([_msg_crit(_PIN_CRITERIA - 1, 0, first, second)], _PIN_CALLS, _PIN_CRITERIA - 1),
    )


def _selftest_verify_section(rec, valid_text):
    """簇 8:验证段的起点(echo "已构建: 行)与逐行比对的宽容度。"""
    crit_a = "grep -q 'a' \"$log\" || exit 1"
    crit_b = "grep -q 'b' \"$log\" || exit 1"
    marker = '  echo "已构建: x"'
    cases = (
        ("起点之前的判据行不计", [crit_a, marker, crit_b], 1),
        ("没有起点就一行也不计", [crit_a, crit_b], 0),
        ("被注释掉的起点不算起点", ['# echo "已构建: 旧"', crit_a, marker, crit_b], 1),
        ("没有冒号的不是起点", ['  echo "已构建 x"', crit_b], 0),
        ("起点行自己不计(哪怕带 || exit 1)", ['  echo "已构建: x" || exit 1', crit_b], 1),
        ("第二个起点不重置", [marker, crit_a, '  echo "已构建: y"', crit_b], 2),
    )
    for name, rows, want in cases:
        rec.eq(f"簇8 {name}", check_text(_join_rows(rows), _LABEL)[2], want)
    rows = valid_text.splitlines()
    padded = ["    " + row + "  \t" if row.strip() else row for row in rows]
    rec.eq("簇8 每行加缩进与行尾空白仍通过", check_text(_join_rows(padded), _LABEL), ([], _PIN_CALLS, _PIN_CRITERIA))
    rec.eq("簇8 CRLF 换行仍通过", check_text(valid_text.replace("\n", "\r\n"), _LABEL), ([], _PIN_CALLS, _PIN_CRITERIA))


# _shell_segments:(名字, 输入的逻辑行, 期望的命令段 [[词, ...], ...])。期望值逐条手算(并用 bash 实测核对过语义)。
# && 与 || 是相邻的两个分隔符,中间夹一个空段;空段里没有词,不影响调用的判断。
_LEX_CASES = (
    # 空白:只有空格与制表符分词(与 shell 一致);NBSP、全角空格是词的一部分
    ("空格分词", "a b c", [["a", "b", "c"]]),
    ("连续多个空格算一处", "a   b", [["a", "b"]]),
    ("制表符分词", "a\tb", [["a", "b"]]),
    ("空格与制表符混用", "a \t b\t c", [["a", "b", "c"]]),
    ("行首行尾的空白不产生词", "  a  ", [["a"]]),
    ("空行只有一个空段", "", [[]]),
    ("只有空白", " \t ", [[]]),
    ("NBSP 不是分隔符", "a\u00a0b", [["a\u00a0b"]]),
    ("全角空格不是分隔符", "a\u3000b", [["a\u3000b"]]),
    # 引号与转义
    ("双引号去掉", '"a"', [["a"]]),
    ("单引号去掉", "'a'", [["a"]]),
    ("双引号里的空格不分词", '"a b"', [["a b"]]),
    ("单引号里的空格不分词", "'a b'", [["a b"]]),
    ("双引号里的制表符不分词", '"a\tb"', [["a\tb"]]),
    ("引号串与相邻字符连成一个词", 'a"b c"d', [["ab cd"]]),
    ("单引号串与相邻字符连成一个词", "a'b c'd", [["ab cd"]]),
    ("两个相邻的引号串连成一个词", "'a'\"b\"", [["ab"]]),
    ("空的双引号是一个空词", '""', [[""]]),
    ("空的单引号是一个空词", "''", [[""]]),
    ("单引号里的双引号是字面", "'a\"b'", [['a"b']]),
    ("双引号里的单引号是字面", '"it\'s"', [["it's"]]),
    ("双引号里的反斜杠加引号", r'"a\"b"', [['a"b']]),
    ("双引号里的两个反斜杠", r'"a\\b"', [[r"a\b"]]),
    ("双引号里其它的反斜杠原样保留", r'"\5"', [[r"\5"]]),
    ("单引号里的反斜杠是字面", r"'a\b'", [[r"a\b"]]),
    ("引号外的反斜杠加空格", r"a\ b", [["a b"]]),
    ("引号外的反斜杠加双引号", r'a\"b', [['a"b']]),
    ("引号外的反斜杠加单引号", r"a\'b", [["a'b"]]),
    ("引号外的两个反斜杠", r"a\\b", [[r"a\b"]]),
    ("引号外的反斜杠加普通字符", r"a\b", [["ab"]]),
    # 带引号、被转义的操作符是普通的词,不切段
    ("双引号里的分号不切段", '"a;b"', [["a;b"]]),
    ("单独的带引号的分号是一个词", '";"', [[";"]]),
    ("带引号的 && 是一个词", '"&&"', [["&&"]]),
    ("带引号的 | 是一个词", "'|'", [["|"]]),
    ("带引号的 & 是一个词", '"&"', [["&"]]),
    ("带引号的括号是两个词", '"(" ")"', [["(", ")"]]),
    ("带引号的重定向符是两个词", '">" "<"', [[">", "<"]]),
    ("带引号的反引号是一个词", '"`"', [["`"]]),
    ("转义的分号是一个词", r"\;", [[";"]]),
    ("转义的 & 与 | 是两个词", r"\& \|", [["&", "|"]]),
    ("转义的括号是两个词", r"\( \)", [["(", ")"]]),
    ("转义的重定向符是两个词", r"\> \<", [[">", "<"]]),
    ("转义的反引号是一个词", r"\`", [["`"]]),
    # $app 的各种写法:去引号之后的样子
    ('带引号的 "$app"', '"$app"', [["$app"]]),
    ('带引号的 "${app}"', '"${app}"', [["${app}"]]),
    ("无引号的 $app", "$app", [["$app"]]),
    ("无引号的 ${app}", "${app}", [["${app}"]]),
    ("单引号的 '$app' 去引号后也是 $app(保守:算调用)", "'$app'", [["$app"]]),
    ("单引号包着带双引号的 \"$app\":双引号是字面,不是 $app", "'\"$app\"'", [['"$app"']]),
    ('"$app" 后紧贴字符连成一个词', '"$app"x', [["$appx"]]),
    ('双引号里 \\$ 保留反斜杠(不是变量展开)', r'"\$app"', [[r"\$app"]]),
    ("$# 与 ${#x} 里的 # 在词中间", "$# ${#x}", [["$#", "${#x}"]]),
    # 注释:引号外、词首的 # 起到行尾
    ("行尾注释", "a # b", [["a"]]),
    ("词首的 # 后面紧贴字也是注释", "a #b", [["a"]]),
    ("单独一个 # 是注释", "a #", [["a"]]),
    ("制表符后的 # 是词首", "a\t# b", [["a"]]),
    ("整行注释没有词", "# a b", [[]]),
    ("缩进的整行注释", "  # a b", [[]]),
    ("引号串之后的空格再 # 是注释", '"a" # b', [["a"]]),
    ("词中间的 # 不是注释", "a#b c", [["a#b", "c"]]),
    ("词尾的 # 不是注释", "a# b", [["a#", "b"]]),
    ("双引号后紧贴的 # 不是注释", '"a"#b', [["a#b"]]),
    ("单引号后紧贴的 # 不是注释", "'a'#b", [["a#b"]]),
    ("转义的空格后的 # 不是注释", r"a\ #b", [["a #b"]]),
    ("冒号开头的词里的 # 不是注释", ":#a b", [[":#a", "b"]]),
    ("转义的 # 是普通字符", r"a \#b", [["a", "#b"]]),
    ("双引号里的 # 不是注释", '"a # b"', [["a # b"]]),
    ("单引号里的 # 不是注释", "'a # b'", [["a # b"]]),
    ("引号里 # 开头的词", '"#a" b', [["#a", "b"]]),
    ("注释里的引号不要求成对", "a # it's \"b", [["a"]]),
    ("注释里的反斜杠不要求成对", "a # b\\", [["a"]]),
    ("注释吃掉后面所有的操作符", "a # b ; c && d", [["a"]]),
    ("注释之前的操作符照常切段", "a ; b # c ; d", [["a"], ["b"]]),
    ("分号后紧贴的 # 是注释", "a;#b", [["a"], []]),
    ("分号后空格再 # 是注释", "a; # b", [["a"], []]),
    ("& 后紧贴的 # 是注释", "a &#b", [["a"], []]),
    ("| 后紧贴的 # 是注释", "a |#b", [["a"], []]),
    ("左括号后紧贴的 # 是注释", "(#a", [[], []]),
    ("右括号后紧贴的 # 是注释", "a)#b", [["a"], []]),
    ("反引号后紧贴的 # 是注释", "a`#b", [["a"], []]),
    # 命令段:引号外的 ; & | ( ) 与反引号
    ("; 切段", "a;b", [["a"], ["b"]]),
    ("; 两侧有空格", "a ; b", [["a"], ["b"]]),
    ("&& 之间夹一个空段", "a && b", [["a"], [], ["b"]]),
    ("&& 紧贴", "a&&b", [["a"], [], ["b"]]),
    ("|| 之间夹一个空段", "a || b", [["a"], [], ["b"]]),
    ("| 切段", "a | b", [["a"], ["b"]]),
    ("| 紧贴", "a|b", [["a"], ["b"]]),
    ("& 切段", "a & b", [["a"], ["b"]]),
    ("& 紧贴", "a&b", [["a"], ["b"]]),
    ("行尾的 & 留一个空段", "a &", [["a"], []]),
    ("行尾的 ; 留一个空段", "a;", [["a"], []]),
    ("行首的 ; 前面是空段", ";a", [[], ["a"]]),
    ("两个分号之间夹一个空段", "a;;b", [["a"], [], ["b"]]),
    ("|& 是两个相邻的操作符", "a |& b", [["a"], [], ["b"]]),
    ("制表符在操作符两侧", "a\t;\tb", [["a"], ["b"]]),
    ("括号切段", "(a b)", [[], ["a", "b"], []]),
    ("$( 里面是独立的一段", "x=$(a b)", [["x=$"], ["a", "b"], []]),
    ("括号紧贴操作符", "(a)&&(b)", [[], ["a"], [], [], [], ["b"], []]),
    ("反引号里面是独立的一段", "x=`a b`", [["x="], ["a", "b"], []]),
    ("五种操作符混用", "a;b&&c|d&e", [["a"], ["b"], [], ["c"], ["d"], ["e"]]),
    # 重定向记号自成一个词、不切段(所以 2>&1 里的 & 不会把调用切开);记号与文件名都留在词表里
    ("2>&1", "a 2>&1 b", [["a", "2", ">&", "1", "b"]]),
    ("1>&2", "a 1>&2", [["a", "1", ">&", "2"]]),
    (">&2", "a >&2", [["a", ">&", "2"]]),
    ("<&3", "a <&3", [["a", "<&", "3"]]),
    ("&>文件", "a &>f", [["a", "&>", "f"]]),
    ("&>>文件", "a &>>f", [["a", "&>>", "f"]]),
    (">|文件", "a >|f", [["a", ">|", "f"]]),
    (">文件", "a >f", [["a", ">", "f"]]),
    (">>文件", "a >>f", [["a", ">>", "f"]]),
    ("<文件", "a <f", [["a", "<", "f"]]),
    ("<<<", "a <<< b", [["a", "<<<", "b"]]),
    ("<>文件", "a <>f", [["a", "<>", "f"]]),
    ("重定向符紧贴前一个词", "a>f", [["a", ">", "f"]]),
    ("重定向符紧贴引号串", '"a">"b"', [["a", ">", "b"]]),
    ("&>> 之后紧跟的 > 另成一个记号", "a &>>>f", [["a", "&>>", ">", "f"]]),
    (">& 之后的 & 仍是操作符", "a >&&b", [["a", ">&"], ["b"]]),
    (">& 之后的 | 仍是操作符", "a >&|b", [["a", ">&"], ["b"]]),
    (">| 之后的 | 仍是操作符", "a >||b", [["a", ">|"], ["b"]]),
    ("重定向后面接分隔符", "a >f; b", [["a", ">", "f"], ["b"]]),
    (">& 之后再接 & 操作符", "a >&2 & b", [["a", ">&", "2"], ["b"]]),
    ("& 与 > 之间有空格:& 是操作符,> 是重定向", "a & >f", [["a"], [">", "f"]]),
    ("2>&1 紧贴 && 操作符", "a 2>&1&&b", [["a", "2", ">&", "1"], [], ["b"]]),
)
# _shell_segments 必须抛 _Unsplittable 的行:引号不成对、行尾孤立的反斜杠
_LEX_BROKEN = (
    ("双引号不成对", 'a "b'),
    ("单引号不成对", "a 'b"),
    ("行首孤立的双引号", '"'),
    ("行首孤立的单引号", "'"),
    ("行尾孤立的反斜杠", "a b\\"),
    ("双引号里被转义的结束引号", r'"a\"'),
    ("单引号里的双引号不能当结束", "'a\""),
    ("双引号里的单引号不能当结束", "\"a'"),
    ("引号串后面跟着孤立的双引号", '"a" "'),
    ("词中间孤立的单引号", "a'b"),
    ("转义的单引号之后又开一个单引号", r"a\''b"),
    ("引号不成对的行里夹着合法的引号串", "a 'b' \"c\" 'd"),
)
# 看上去像不成对、其实合法的行:不能抛
_LEX_FINE = (
    ("行尾两个反斜杠是一个转义的反斜杠", "a b\\\\"),
    ("注释里的单引号", "a # it's"),
    ("双引号里的单引号", "\"'\""),
    ("单引号里的双引号", "'\"'"),
    ("双引号里被转义的双引号", r'"\""'),
    ("转义的引号", r"\' \""),
)


def _lex_broken(line):
    try:
        _shell_segments(line)
    except _Unsplittable:
        return True
    return False


def _selftest_lexer(rec):
    """簇 9:词法(_shell_segments)逐项钉住——空白、引号、转义、注释、命令段、重定向、不成对的引号。"""
    for name, line, want in _LEX_CASES:
        rec.eq(f"簇9 {name}", _shell_segments(line), want)
    for name, line in _LEX_BROKEN:
        rec.eq(f"簇9 {name}:拆不开", _lex_broken(line), True)
    for name, line in _LEX_FINE:
        rec.eq(f"簇9 {name}:拆得开", _lex_broken(line), False)


def _selftest_comments(rec):
    """簇 10:注释(缺陷 D1)——引号外、词首的 # 起到行尾不是命令的参数;词中间、引号里、转义的 # 不是注释;多行调用里注释之后的续行也一样。"""
    bad = [_msg_sim(1)]
    cases = (
        # 参数写在注释里:不算数
        ("参数在行尾注释里", _CALL + " # -dnfquitaftersim 5", (bad, 1)),
        ("参数紧贴 # 写在注释里", _CALL + " #-dnfquitaftersim 5", (bad, 1)),
        ("制表符之后的 # 起注释", _CALL + "\t# -dnfquitaftersim 5", (bad, 1)),
        ("分号之后紧贴的 # 起注释", _CALL + ";# -dnfquitaftersim 5", (bad, 1)),
        ("其它参数之后才是注释", '"$app" -batchmode -dnftimescale 4 -dnfquitafter 9 # -dnfquitaftersim 5', (bad, 1)),
        ("注释里是参数名、值在更后面", _CALL + " # -dnfquitaftersim # 5", (bad, 1)),
        # 参数在注释之前:注释只是说明,不影响判断
        ("参数在前、行尾带说明", _GOOD + " # 说明", ([], 1)),
        ("参数在前、行尾只有一个 #", _GOOD + " #", ([], 1)),
        ("参数在前、# 紧贴说明", _GOOD + " #说明", ([], 1)),
        ("参数在前、制表符之后是注释", _GOOD + "\t# 说明", ([], 1)),
        ("参数在 -dnftimescale 之前、行尾带说明", '"$app" -dnfquitaftersim 5 -dnftimescale 4 # 说明', ([], 1)),
        ("注释里有单引号", _GOOD + " # don't", ([], 1)),
        ("注释里有双引号", _GOOD + ' # 说 "x', ([], 1)),
        ("注释里有分号与 &&", _GOOD + " # a; b && c", ([], 1)),
        ("参数在前、后面是带 # 的双引号字符串", _GOOD + ' -dnfnote "a # b"', ([], 1)),
        ("参数在前、后面是带 # 的单引号字符串", _GOOD + " -dnfnote 'a # b'", ([], 1)),
        # 词中间的 # 不是注释
        ("值后面紧贴 #x:值不是数", _CALL + " -dnfquitaftersim 5#x", (bad, 1)),
        ("值后面紧贴 #:值不是数", _CALL + " -dnfquitaftersim 5#", (bad, 1)),
        ("参数名后面紧贴 #:不是这个参数名", _CALL + " -dnfquitaftersim# 5", (bad, 1)),
        ("词中间的 # 不吃掉后面的参数", '"$app" -dnftimescale 4 -dnfnote a#b -dnfquitaftersim 5', ([], 1)),
        ("$# 里的 # 不吃掉后面的参数", '"$app" -dnftimescale 4 -dnfnote $# -dnfquitaftersim 5', ([], 1)),
        # 引号里的 # 不是注释;引号里的参数名不是独立的词
        ("双引号里有 #、后面还有参数", _CALL + ' -dnfnote "a # b" -dnfquitaftersim 5', ([], 1)),
        ("单引号里有 #、后面还有参数", _CALL + " -dnfnote 'a # b' -dnfquitaftersim 5", ([], 1)),
        ("# 开头的带引号的词不是注释", _CALL + ' -dnfnote "#x" -dnfquitaftersim 5', ([], 1)),
        ("参数名在双引号字符串里:不是独立的词", _CALL + ' -dnfnote "# -dnfquitaftersim 5"', (bad, 1)),
        ("参数名在单引号字符串里:不是独立的词", _CALL + " -dnfnote '# -dnfquitaftersim 5'", (bad, 1)),
        ("双引号字符串里只有参数名与值", _CALL + ' -dnfnote "-dnfquitaftersim 5"', (bad, 1)),
        # 转义的 # 是普通字符
        ("转义的 # 是普通参数", _GOOD + r" \#x", ([], 1)),
        ("转义的 # 不吃掉后面的参数", _CALL + r" \#x -dnfquitaftersim 5", ([], 1)),
        ("转义的空格后的 # 不是注释", _CALL + r" a\ #b -dnfquitaftersim 5", ([], 1)),
        ("冒号开头的词里的 # 不是注释", _CALL + " :#x -dnfquitaftersim 5", ([], 1)),
    )
    for name, text, want in cases:
        rec.eq(f"簇10 {name}", _verdict(text), want)
    # 多行调用:行号是调用的第一个物理行;注释吃掉它之后的续行内容
    multi = (
        ("续行之后才有注释、参数在注释之前", ['"$app" -batchmode \\', "  -dnftimescale 4 -dnfquitaftersim 5 # 说明"], ([], 1)),
        ("参数在注释之后的续行里:不算数", ['"$app" -batchmode -dnftimescale 4 # 说明 \\', "  -dnfquitaftersim 5"], (bad, 1)),
        ("注释夹在续行中间、参数在它后面:不算数", ['"$app" -dnftimescale 4 \\', "  # 说明 \\", "  -dnfquitaftersim 5"], (bad, 1)),
        ("注释在续行末尾、参数在它前面", ['"$app" -dnftimescale 4 \\', "  -dnfquitaftersim 5 \\", "  # 说明"], ([], 1)),
        ("注释及其后的续行内容不算词、参数在注释之前", ['"$app" -batchmode \\', "  -dnftimescale 4 -dnfquitaftersim 5 # 说明 \\", "  -dnfquitafter 100", "echo 尾"], ([], 1)),
        ("错误行号是调用的第一个物理行", ["echo 头", '"$app" -batchmode -dnftimescale 4 # 说明 \\', "  -dnfquitaftersim 5"], ([_msg_sim(2)], 1)),
        ("参数在第三个物理行的注释里:行号仍是第一个物理行", ["echo 头", '"$app" -dnftimescale 4 \\', "  -batchmode # -dnfquitaftersim 5"], ([_msg_sim(2)], 1)),
    )
    for name, rows, want in multi:
        rec.eq(f"簇10 多行:{name}", _verdict(_join_rows(rows)), want)
    # 注释行里的任何东西(包括不成对的引号)都不算调用、不报无法拆分
    ok = _sim_text("5")
    comment_lines = (
        ("整行注释里有 -dnftimescale 与不成对的单引号", "# 别忘了 -dnftimescale 4 要配 -dnfquitaftersim,it's\n"),
        ("缩进的整行注释里有不成对的双引号", '    # "$app" -dnftimescale 4 "x\n'),
    )
    for name, text in comment_lines:
        errs, calls, _ = check_text(text + ok, _LABEL)
        rec.eq(f"簇10 {name}:不算调用也不报无法拆分", (_only(errs, _CAT_SIM), _only(errs, _CAT_NOSPLIT), calls), ([], [], 1))


_OPS = (";", "&&", "||", "|", "&")  # 命令段的五种分隔操作符


def _selftest_segments(rec):
    """簇 11:命令段(缺陷 D1b)——参数必须与 $app 在同一个命令段;; && || | & 都切段,两侧有无空白、用空格还是制表符都一样。"""
    bad = [_msg_sim(1)]
    for op in _OPS:
        rec.eq(f"簇11 参数在 {op} 之后的别的命令里", _verdict(f"{_CALL} {op} echo -dnfquitaftersim 5"), (bad, 1))
        rec.eq(f"簇11 参数在 {op} 之前的别的命令里", _verdict(f"echo -dnfquitaftersim 5 {op} {_CALL}"), (bad, 1))
        rec.eq(f"簇11 {op} 紧贴(无空白)时参数在别的命令里", _verdict(f"{_CALL}{op}echo -dnfquitaftersim 5"), (bad, 1))
        rec.eq(f"簇11 {op} 两侧是制表符时参数在别的命令里", _verdict(f"{_CALL}\t{op}\techo -dnfquitaftersim 5"), (bad, 1))
        rec.eq(f"簇11 {op} 之后的别的命令不影响好的调用", _verdict(f"{_GOOD} {op} echo x"), ([], 1))
        rec.eq(f"簇11 {op} 之前的别的命令不影响好的调用", _verdict(f"echo x {op} {_GOOD}"), ([], 1))
        rec.eq(f"簇11 {op} 紧贴时好的调用", _verdict(f"echo x{op}{_GOOD}"), ([], 1))
        rec.eq(f"簇11 {op} 把 -dnfquitaftersim 与它的值隔开:不是连续的词", _verdict(f"{_CALL} -dnfquitaftersim {op} echo 5"), (bad, 1))
    # 带引号、被转义的操作符不是分隔符:同一段里的参数照常算
    quoted = (
        ('带引号的 ";" 是一个普通的词', _CALL + ' -dnfnote ";" -dnfquitaftersim 5', ([], 1)),
        ('带引号的 "&&" 是一个普通的词', _CALL + ' -dnfnote "&&" -dnfquitaftersim 5', ([], 1)),
        ("带引号的 | 是一个普通的词", _CALL + " -dnfnote '|' -dnfquitaftersim 5", ([], 1)),
        ('带引号的 "&" 是一个普通的词', _CALL + ' -dnfnote "&" -dnfquitaftersim 5', ([], 1)),
        ("转义的分号是一个普通的词", _CALL + r" -dnfnote \; -dnfquitaftersim 5", ([], 1)),
        ('引号里带分号的字符串:"a;b"', _CALL + ' -dnfnote "a;b" -dnfquitaftersim 5', ([], 1)),
        ('参数值本身是带引号的分号:"-dnfquitaftersim" ";"', _CALL + ' "-dnfquitaftersim" ";"', (bad, 1)),
    )
    for name, text, want in quoted:
        rec.eq(f"簇11 {name}", _verdict(text), want)
    # 括号与反引号也限定一个命令的范围
    scoped = (
        ("子 shell 里的好调用", f"({_GOOD})", ([], 1)),
        ("子 shell 两侧有空格", f"( {_GOOD} )", ([], 1)),
        ("子 shell 里缺参数", f"({_CALL})", (bad, 1)),
        ("右括号之后的参数不属于调用", f"({_CALL}) -dnfquitaftersim 5", (bad, 1)),
        ("右括号紧贴的调用参数在括号之后", f"echo ({_CALL})-dnfquitaftersim 5", (bad, 1)),
        ("命令替换 $( 里的好调用", f"x=$({_GOOD})", ([], 1)),
        ("命令替换 $( 里缺参数", f"x=$({_CALL})", (bad, 1)),
        ("反引号里的好调用", f"x=`{_GOOD}`", ([], 1)),
        ("反引号里缺参数", f"x=`{_CALL}`", (bad, 1)),
        ("反引号之后的参数不属于调用", f"x=`{_CALL}` -dnfquitaftersim 5", (bad, 1)),
    )
    for name, text, want in scoped:
        rec.eq(f"簇11 {name}", _verdict(text), want)
    # 重定向不切段:重定向记号与文件名夹在中间,同一个命令的参数仍然算数
    redirects = (
        ("重定向到文件再 2>&1、参数在前", _GOOD + " > out.log 2>&1", ([], 1)),
        ("2>&1 在 $app 之后、参数之前", '"$app" 2>&1 -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("2>&1 在两个参数之间", '"$app" -dnftimescale 4 2>&1 -dnfquitaftersim 5', ([], 1)),
        ("2>&1 紧贴在参数之后", _GOOD + " 2>&1", ([], 1)),
        ("2>&1 之后缺参数:仍是调用", '"$app" 2>&1 -dnftimescale 4', (bad, 1)),
        ("&> 不切段", '"$app" &>/dev/null -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("&>> 不切段", '"$app" &>>out -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        (">| 不切段", '"$app" >|out -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("<& 不切段", '"$app" <&3 -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        (">& 不切段", '"$app" >&2 -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("> 紧贴 $app 之后", '"$app">out -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("< 紧贴 $app 之后", '"$app"<in -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("后台运行:行尾的 &", _GOOD + " &", ([], 1)),
        ("重定向夹在参数名与值之间:不连续(宁可误报)", '"$app" -dnftimescale 4 -dnfquitaftersim >out 5', (bad, 1)),
        ("重定向之后的 & 是操作符:其后的命令不属于调用", f"{_CALL} >out & echo -dnfquitaftersim 5", (bad, 1)),
        ("& 与 > 之间有空格:& 是操作符", f"{_CALL} & >out -dnfquitaftersim 5", (bad, 1)),
    )
    for name, text, want in redirects:
        rec.eq(f"簇11 {name}", _verdict(text), want)


def _selftest_multi_call(rec):
    """簇 12:一行里的多个调用(缺陷 D2)——按调用计数、逐个检查;错误逐个报,行号都是这条逻辑行的第一个物理行。"""
    one, two = [_msg_sim(1)], [_msg_sim(1), _msg_sim(1)]
    for op in _OPS:
        for sep in (f" {op} ", op):
            shown = "有空格" if sep != op else "紧贴"
            combos = (
                ("坏 + 好", f"{_CALL}{sep}{_GOOD}", (one, 2)),
                ("好 + 坏", f"{_GOOD}{sep}{_CALL}", (one, 2)),
                ("坏 + 坏:两条错", f"{_CALL}{sep}{_CALL}", (two, 2)),
                ("好 + 好:两个调用", f"{_GOOD}{sep}{_GOOD}", ([], 2)),
            )
            for name, text, want in combos:
                rec.eq(f"簇12 {op} 连接两个调用({shown}):{name}", _verdict(text), want)
    three = (
        ("坏 好 坏", f"{_CALL} ; {_GOOD} ; {_CALL}", (two, 3)),
        ("好 坏 好", f"{_GOOD} && {_CALL} || {_GOOD}", (one, 3)),
        ("好 好 好", f"{_GOOD} | {_GOOD} & {_GOOD}", ([], 3)),
        ("坏 坏 坏:三条错", f"{_CALL} ; {_CALL} | {_CALL}", ([_msg_sim(1)] * 3, 3)),
    )
    for name, text, want in three:
        rec.eq(f"簇12 三个调用:{name}", _verdict(text), want)
    # 行号:每条错误都带这条逻辑行的第一个物理行;连续行拼成的一条逻辑行也一样
    rows = ["echo 头", f"{_CALL} ; {_CALL}", "echo 中", f"{_GOOD} ; {_CALL}", _sim_text("5")]
    rec.eq("簇12 多行文本里每条错误的行号", _verdict(_join_rows(rows)), ([_msg_sim(2), _msg_sim(2), _msg_sim(4)], 5))
    rows = ["echo 头", '"$app" -dnftimescale 4 \\', '  ; "$app" -dnftimescale 4 \\', "  -batchmode"]
    rec.eq("簇12 连续行拼成的逻辑行里的两个调用:行号是第一个物理行", _verdict(_join_rows(rows)), ([_msg_sim(2), _msg_sim(2)], 2))
    rows = [f"{_GOOD} ; {_CALL}", f"{_CALL} ; {_GOOD}"]
    rec.eq("簇12 两行各有两个调用", _verdict(_join_rows(rows)), ([_msg_sim(1), _msg_sim(2)], 4))
    # 计数的单位是调用(命令段),不是逻辑行、不是 $app 词、不是 -dnftimescale 词
    units = (
        ("同一段里两个 $app 词只算一个调用", '"$app" "$app" -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("同一段里 $app 与 ${app} 各一个、缺参数:一个调用一条错", '"$app" "${app}" -dnftimescale 4', (one, 1)),
        ("同一段里两个 -dnftimescale 只算一个调用", '"$app" -dnftimescale 4 -dnftimescale 5 -dnfquitaftersim 5', ([], 1)),
        ("同一段里两个 -dnftimescale、缺参数:只报一条", '"$app" -dnftimescale 4 -dnftimescale 5', (one, 1)),
        ("有 $app 没有 -dnftimescale 的段不计数", f'"$app" -batchmode ; {_GOOD}', ([], 1)),
        ("有 -dnftimescale 没有 $app 的段不计数", f"echo -dnftimescale 4 ; {_GOOD}", ([], 1)),
        ("$app 与 -dnftimescale 分在两个段:都不是调用", '"$app" -batchmode ; echo -dnftimescale 4', ([], 0)),
        ("-dnftimescale 在前、$app 在后的段也是调用", '-dnftimescale 4 "$app"', (one, 1)),
        ("-dnftimescale 在前、$app 在后、带参数", '-dnftimescale 4 "$app" -dnfquitaftersim 5', ([], 1)),
    )
    for name, text, want in units:
        rec.eq(f"簇12 {name}", _verdict(text), want)


def _selftest_spellings(rec):
    """簇 13:各种写法——带引号的参数名(B1)、"${app}" 与无引号的 $app(B2)、制表符分隔(B3)都是调用;
    $app2、${app}x、$apple 之类不是调用;-dnfquitaftersim 的值与参数名的边界;重复与不连续。"""
    one = [_msg_sim(1)]
    # B1:参数名带引号,去引号之后就是同一个词
    quoted_flags = (
        ('B1 双引号 "-dnftimescale"、缺参数', '"$app" "-dnftimescale" 4', (one, 1)),
        ("B1 单引号 '-dnftimescale'、缺参数", "\"$app\" '-dnftimescale' 4", (one, 1)),
        ('B1 双引号 "-dnftimescale"、带参数', '"$app" "-dnftimescale" 4 -dnfquitaftersim 5', ([], 1)),
        ("B1 单引号 '-dnftimescale'、带参数", "\"$app\" '-dnftimescale' 4 -dnfquitaftersim 5", ([], 1)),
        ('B1 双引号 "-dnfquitaftersim"', '"$app" -dnftimescale 4 "-dnfquitaftersim" 5', ([], 1)),
        ("B1 单引号 '-dnfquitaftersim'", "\"$app\" -dnftimescale 4 '-dnfquitaftersim' 5", ([], 1)),
        ('B1 参数名与值都带双引号', '"$app" -dnftimescale 4 "-dnfquitaftersim" "5"', ([], 1)),
        ("B1 参数名与值都带单引号", "\"$app\" -dnftimescale 4 '-dnfquitaftersim' '5'", ([], 1)),
        ('B1 参数名被引号切成两半', '"$app" -dnftimescale 4 -dnfquit"aftersim" 5', ([], 1)),
        ('B1 值被引号切成两半:50', '"$app" -dnftimescale 4 -dnfquitaftersim 5"0"', ([], 1)),
        ('B1 带引号的值 "0" 不是正数', '"$app" -dnftimescale 4 -dnfquitaftersim "0"', (one, 1)),
        ('B1 空值 ""', '"$app" -dnftimescale 4 -dnfquitaftersim ""', (one, 1)),
        ('B1 值带前导空格 " 5":不是数', '"$app" -dnftimescale 4 -dnfquitaftersim " 5"', (one, 1)),
        ('B1 值带尾随空格 "5 ":不是数', '"$app" -dnftimescale 4 -dnfquitaftersim "5 "', (one, 1)),
        ('B1 -dnftimescale 的值与 $app 之间还有别的参数', '"$app" -batchmode "-dnftimescale" 4 -dnfquitafter 5', (one, 1)),
    )
    for name, text, want in quoted_flags:
        rec.eq(f"簇13 {name}", _verdict(text), want)
    # B2:$app 的写法;去引号之后恰好是 $app 或 ${app} 的词才是调用
    app_forms = (
        ('B2 "${app}"、缺参数', '"${app}" -dnftimescale 4', (one, 1)),
        ('B2 "${app}"、带参数', '"${app}" -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("B2 无引号 $app、缺参数", "$app -dnftimescale 4", (one, 1)),
        ("B2 无引号 $app、带参数", "$app -dnftimescale 4 -dnfquitaftersim 5", ([], 1)),
        ("B2 无引号 ${app}、缺参数", "${app} -dnftimescale 4", (one, 1)),
        ("B2 无引号 ${app}、带参数", "${app} -dnftimescale 4 -dnfquitaftersim 5", ([], 1)),
        ("B2 单引号 '$app'(保守:算调用)、缺参数", "'$app' -dnftimescale 4", (one, 1)),
        ("B2 前面有环境变量赋值与 env", 'env X=1 "$app" -dnftimescale 4', (one, 1)),
        ("B2 前面有 exec", 'exec "$app" -dnftimescale 4', (one, 1)),
        ("B2 前面有变量赋值、带参数", 'X=1 "$app" -dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
    )
    for name, text, want in app_forms:
        rec.eq(f"簇13 {name}", _verdict(text), want)
    # 不是调用:这些行里 -dnftimescale 缺参数,若误当成调用就会多出一条错;调用数必须是 0
    not_calls = (
        ('"$app2"', '"$app2" -dnftimescale 4'),
        ('"${app}x"', '"${app}x" -dnftimescale 4'),
        ("$apple", "$apple -dnftimescale 4"),
        ('"$APP"', '"$APP" -dnftimescale 4'),
        ('"${app2}"', '"${app2}" -dnftimescale 4'),
        ('"${app:-x}"', '"${app:-x}" -dnftimescale 4'),
        ('"$app"x(与后面的字符连成一个词)', '"$app"x -dnftimescale 4'),
        ('x"$app"(与前面的字符连成一个词)', 'x"$app" -dnftimescale 4'),
        ('"\\$app"(反斜杠转义了 $)', r'"\$app" -dnftimescale 4'),
        ("单引号里的双引号 '\"$app\"'", "'\"$app\"' -dnftimescale 4"),
        ("$ 与 app 之间有空格", "$ app -dnftimescale 4"),
        ("没有 $ 的 app", "app -dnftimescale 4"),
        ('"$@"', '"$@" -dnftimescale 4'),
        ('"$app -dnftimescale" 在同一个带引号的词里', '"$app -dnftimescale" 4'),
        ("-dnftimescale=4 不是 -dnftimescale 这个词", '"$app" -dnftimescale=4'),
        ("--dnftimescale 不是 -dnftimescale 这个词", '"$app" --dnftimescale 4'),
        ("-dnftimescale 之后紧贴别的字符", '"$app" -dnftimescale4'),
    )
    for name, text in not_calls:
        rec.eq(f"簇13 不是调用:{name}", _verdict(text), ([], 0))
    # B3:制表符分隔,与空格一样;NBSP、全角空格不是分隔符
    tabs = (
        ("B3 制表符分隔的缺参数调用", '"$app"\t-dnftimescale\t4', (one, 1)),
        ("B3 制表符分隔的带参数调用", '"$app"\t-dnftimescale\t4\t-dnfquitaftersim\t5', ([], 1)),
        ("B3 只有参数名与值之间是制表符", '"$app" -dnftimescale 4 -dnfquitaftersim\t5', ([], 1)),
        ("B3 参数名与值之间是空格加制表符加空格", '"$app" -dnftimescale 4 -dnfquitaftersim \t 5', ([], 1)),
        ("B3 只有 -dnftimescale 之前是制表符", '"$app"\t-dnftimescale 4 -dnfquitaftersim 5', ([], 1)),
        ("B3 只有 -dnfquitaftersim 之前是制表符", '"$app" -dnftimescale 4\t-dnfquitaftersim 5', ([], 1)),
        ("B3 -dnftimescale 与它的值之间是制表符、值非正", '"$app" -dnftimescale\t4 -dnfquitaftersim 0', (one, 1)),
        ("B3 行首行尾的制表符", '\t"$app"\t-dnftimescale\t4\t', (one, 1)),
        ("B3 NBSP 在参数名与值之间:粘成一个词", '"$app" -dnftimescale 4 -dnfquitaftersim\u00a05', (one, 1)),
        ("B3 全角空格在参数名与值之间:粘成一个词", '"$app" -dnftimescale 4 -dnfquitaftersim\u30005', (one, 1)),
        ("B3 NBSP 在 $app 与 -dnftimescale 之间:粘成一个词,不是调用", '"$app"\u00a0-dnftimescale 4 -dnfquitaftersim 5', ([], 0)),
    )
    for name, text, want in tabs:
        rec.eq(f"簇13 {name}", _verdict(text), want)
    # -dnfquitaftersim 的值:数字边界(ASCII 数字、至多一个小数点两侧都有数字、至少一位非零)
    for value in ("9", "0.9", "90", "19.91", "10", "100.0", "0.10", "01", "000.5", "0.000001", "1.0", "0.01", "99999", "3.14159"):
        rec.eq(f"簇13 值 {value} 是正数", _verdict(_sim_text(value)), ([], 1))
    for value in ("+5", "5e1", "0x5", "5,5", "--5", "5-", "5.0.", ".", "..", "0.", "0..5", "1..5", "\u0663", "\uff15", "5_0", "0e0", "5:", "5A", "5.5A", "5.5:", "5:.5"):
        rec.eq(f"簇13 值 {value} 不是正数", _verdict(_sim_text(value)), (one, 1))
    # 重复出现:有一对合格(参数名之后紧跟严格正数)就行,与宿主"非正数的值被忽略、较晚的合格值生效"一致
    dup = (
        ("先不合格后合格", '"$app" -dnftimescale 4 -dnfquitaftersim 0 -dnfquitaftersim 5', ([], 1)),
        ("先合格后不合格", '"$app" -dnftimescale 4 -dnfquitaftersim 5 -dnfquitaftersim 0', ([], 1)),
        ("两个都不合格", '"$app" -dnftimescale 4 -dnfquitaftersim 0 -dnfquitaftersim 0', (one, 1)),
        ("第一个的值是第二个参数名", '"$app" -dnftimescale 4 -dnfquitaftersim -dnfquitaftersim 5', ([], 1)),
        ("两个都合格", '"$app" -dnftimescale 4 -dnfquitaftersim 5 -dnfquitaftersim 6', ([], 1)),
    )
    for name, text, want in dup:
        rec.eq(f"簇13 重复:{name}", _verdict(text), want)
    # 参数名与值必须是连续的两个词;参数名必须逐字相同(区分大小写)
    adjacent = (
        ("中间隔着别的参数", '"$app" -dnftimescale 4 -dnfquitaftersim -batchmode 5', one),
        ("值在参数名之前", '"$app" -dnftimescale 4 5 -dnfquitaftersim', one),
        ("参数名与值用等号连接", '"$app" -dnftimescale 4 -dnfquitaftersim=5', one),
        ("参数名多一个短横", '"$app" -dnftimescale 4 --dnfquitaftersim 5', one),
        ("参数名大写", '"$app" -dnftimescale 4 -DNFQUITAFTERSIM 5', one),
        ("参数名少一个字母", '"$app" -dnftimescale 4 -dnfquitaftersi 5', one),
        ("-dnfquitaftersim 是最后一个词、后面没有值", '"$app" -dnftimescale 4 -dnfquitaftersim', one),
    )
    for name, text, want in adjacent:
        rec.eq(f"簇13 {name}", _verdict(text), (want, 1))


def _check_rows(rows, edits):
    """把真实脚本的若干物理行改掉后整体检查:edits 是 [(行下标, 旧串, 新串)];旧串必须真的在那一行里,
    否则这条变体是空操作(会悄悄通过),抛 AssertionError 让该簇报失败。"""
    changed = list(rows)
    for index, old, new in edits:
        assert old in changed[index], (index, old)
        changed[index] = changed[index].replace(old, new, 1)
    return check_text(_join_rows(changed), _LABEL)


def _selftest_fail_loud(rec):
    """簇 14:失败要响——含 -dnftimescale 却无法按 shell 语法拆分的逻辑行必须报错(不悄悄跳过),其余行照常检查;
    不含 -dnftimescale 的行不拆词、不报错。合成文本总会多出一条"通过判据行与基线不一致",所以整个返回值逐条比对。"""
    ok = _sim_text("5")
    no_criteria = _msg_crit(0, 0, _FIRST_CRITERION, "<EOF>")
    broken = (
        ("双引号不成对", '"$app" -dnftimescale 4 -dnfquitaftersim 5 "'),
        ("单引号不成对", "\"$app\" -dnftimescale 4 -dnfquitaftersim 5 '"),
        ("被转义的结束引号", r'"$app" -dnftimescale 4 -dnfquitaftersim 5 "a\"'),
        ("-dnftimescale 在不成对的引号里", '"$app" "-dnftimescale 4 -dnfquitaftersim 5'),
        ("-dnftimescale 自己带着不成对的引号", '"$app" -dnftimescale" 4 -dnfquitaftersim 5'),
        ("引号里的 # 不是注释,引号仍不成对", '"$app" -dnftimescale 4 "x # -dnfquitaftersim 5'),
        ("更长的参数名也含该子串", '"$app" -dnftimescalefoo "x'),
        ("echo 里的记号也要响", 'echo -dnftimescale "x'),
        ("子串出现在词中间", 'x-dnftimescale "x'),
    )
    for name, line in broken:
        want = ([_msg_nosplit(2), no_criteria], 2, 0)
        rec.eq(f"簇14 {name}:整行报一条无法拆分、前后的调用照常数", check_text(_join_rows([ok, line, ok]), _LABEL), want)
    bad_line = broken[0][1]
    rec.eq("簇14 坏行排在最前面", check_text(_join_rows([bad_line, ok]), _LABEL), ([_msg_nosplit(1), no_criteria], 1, 0))
    rec.eq(
        "簇14 只有坏行:不算调用,先报无法拆分再报未找到任何",
        check_text(_join_rows([bad_line]), _LABEL),
        ([_msg_nosplit(1), _msg_no_call(), no_criteria], 0, 0),
    )
    rec.eq(
        "簇14 两条坏行各报一条",
        check_text(_join_rows([bad_line, ok, broken[1][1]]), _LABEL),
        ([_msg_nosplit(1), _msg_nosplit(3), no_criteria], 1, 0),
    )
    rows = ["echo 头", '"$app" -dnftimescale 4 \\', '  -dnfquitaftersim 5 "', ok]
    rec.eq("簇14 连续行拼成的坏行:行号是第一个物理行", check_text(_join_rows(rows), _LABEL), ([_msg_nosplit(2), no_criteria], 1, 0))
    rows = [bad_line, _sim_text("0"), ok]
    rec.eq(
        "簇14 坏行之后缺参数的调用仍被检查",
        check_text(_join_rows(rows), _LABEL),
        ([_msg_nosplit(1), _msg_sim(2), no_criteria], 2, 0),
    )
    silent = (
        ("不含 -dnftimescale 的双引号不成对", 'echo "x'),
        ("不含 -dnftimescale 的单引号不成对", "echo 'x"),
        ("近似子串 -dnftimescal(少一个字母)", '"$app" -dnftimescal "x'),
        ("近似子串 dnftimescale(没有短横)", '"$app" dnftimescale "x'),
        ("近似子串 -DNFTIMESCALE(大写)", '"$app" -DNFTIMESCALE "x'),
        ("近似子串 -dnf timescale(被空格隔开)", '"$app" -dnf timescale "x'),
        ("整行注释里的不成对引号", "# -dnftimescale it's"),
        ("缩进的整行注释里的不成对引号", '    # "$app" -dnftimescale 4 "x'),
    )
    for name, line in silent:
        rec.eq(f"簇14 {name}:不报错", check_text(_join_rows([line, ok]), _LABEL), ([no_criteria], 1, 0))
    rec.eq(
        "簇14 好调用的行尾注释里有不成对引号:仍是调用、不报错",
        check_text(_join_rows([_GOOD + " # it's"]), _LABEL),
        ([no_criteria], 1, 0),
    )


def _selftest_real_variants(rec, valid_text):
    """簇 15:真实脚本里的每一个调用都被绕过或换写法——逐个做,整个返回值逐条比对,行号是该调用的起始物理行(1 起算)。
    绕过:在 -dnfquitaftersim 之前加 # 与五种命令分隔,或去掉参数再换 -dnftimescale、$app 的写法,都恰有一条缺少正数;
    换写法:${app}、无引号 $app、带引号的 -dnftimescale、制表符、行尾注释与重定向,仍然通过;行尾多一个引号则整行报无法拆分。"""
    rows = valid_text.splitlines()
    spans = _call_spans(rows)
    rec.eq("簇15 真实脚本里的调用数", len(spans), _PIN_CALLS)
    for n, (first, last) in enumerate(spans, start=1):
        sim_rows = [r for r in range(first, last + 1) if "-dnfquitaftersim" in rows[r]]
        app_rows = [r for r in range(first, last + 1) if '"$app"' in rows[r]]
        scale_rows = [r for r in range(first, last + 1) if "-dnftimescale" in rows[r]]
        shape = (len(sim_rows), len(app_rows), len(scale_rows))
        rec.eq(f"簇15 第 {n} 个调用的三个记号各只在一个物理行上", shape, (1, 1, 1))
        if shape != (1, 1, 1):
            continue
        sim_r, app_r, scale_r = sim_rows[0], app_rows[0], scale_rows[0]
        sim = re.search(r"-dnfquitaftersim\s+\S+", rows[sim_r]).group()
        drop = (sim_r, " " + sim, "")
        missing = ([_msg_sim(first + 1)], _PIN_CALLS, _PIN_CRITERIA)
        passing = ([], _PIN_CALLS, _PIN_CRITERIA)
        bypasses = (
            ("在 -dnfquitaftersim 之前加 # ", [(sim_r, "-dnfquitaftersim", "# -dnfquitaftersim")]),
            ("在 -dnfquitaftersim 之前加 ; ", [(sim_r, "-dnfquitaftersim", "; -dnfquitaftersim")]),
            ("在 -dnfquitaftersim 之前加 && echo ", [(sim_r, "-dnfquitaftersim", "&& echo -dnfquitaftersim")]),
            ("在 -dnfquitaftersim 之前加 || echo ", [(sim_r, "-dnfquitaftersim", "|| echo -dnfquitaftersim")]),
            ("在 -dnfquitaftersim 之前加 | echo ", [(sim_r, "-dnfquitaftersim", "| echo -dnfquitaftersim")]),
            ("在 -dnfquitaftersim 之前加 & echo ", [(sim_r, "-dnfquitaftersim", "& echo -dnfquitaftersim")]),
            ("去掉参数、-dnftimescale 加双引号", [drop, (scale_r, "-dnftimescale", '"-dnftimescale"')]),
            ("去掉参数、$app 写成 ${app}", [drop, (app_r, '"$app"', '"${app}"')]),
            ("去掉参数、$app 去掉引号", [drop, (app_r, '"$app"', "$app")]),
            ("去掉参数、-dnftimescale 与值之间是制表符", [drop, (scale_r, "-dnftimescale ", "-dnftimescale\t")]),
        )
        for name, edits in bypasses:
            rec.eq(f"簇15 第 {n} 个调用 {name}", _check_rows(rows, edits), missing)
        variants = (
            ('"$app" 写成 "${app}"', [(app_r, '"$app"', '"${app}"')]),
            ("$app 去掉引号", [(app_r, '"$app"', "$app")]),
            ("-dnftimescale 加双引号", [(scale_r, "-dnftimescale", '"-dnftimescale"')]),
            ("-dnfquitaftersim 与值之间是制表符", [(sim_r, "-dnfquitaftersim ", "-dnfquitaftersim\t")]),
            ("行尾加注释", [(last, rows[last], rows[last] + " # 说明")]),
            ("行尾加重定向", [(last, rows[last], rows[last] + " 2>&1")]),
        )
        for name, edits in variants:
            rec.eq(f"簇15 第 {n} 个调用 {name}:仍通过", _check_rows(rows, edits), passing)
        broken = ([_msg_nosplit(first + 1)], _PIN_CALLS - 1, _PIN_CRITERIA)
        rec.eq(f"簇15 第 {n} 个调用行尾多一个引号:整行报无法拆分", _check_rows(rows, [(last, rows[last], rows[last] + ' "')]), broken)


def _selftest_harness(rec):
    """簇 16:自测基础设施本身——_Recorder 记条数与失败、_cluster 把异常记成失败、_cluster_gaps 发现没跑的簇。
    这几处失灵时别的簇会悄悄全部通过,所以单独钉住。"""
    # _Recorder.eq 不能用 rec.eq 来检查(它若不记失败,检查结果也被一起吞掉):这里直接抛异常,交给 _cluster 记成失败
    probe = _Recorder()
    fresh = (probe.checks, probe.failures, probe.last, probe.clusters)
    if fresh != (0, [], "(尚无)", []):
        raise AssertionError(f"新建的记录器不是空的:{fresh!r}")
    probe.eq("甲", 1, 1)
    probe.eq("乙", 1, 2)
    state = (probe.checks, probe.failures, probe.last)
    if state != (2, [("乙", 2, 1)], "乙"):
        raise AssertionError(f"_Recorder.eq 的记录不对(应为 2 条、失败只记 乙 的名字/期望/实际、最近一条是 乙):{state!r}")

    def quiet(r, extra):
        r.eq("丙", extra, extra)
        r.eq("丁", extra, extra)

    def idle(r):
        pass

    probe = _Recorder()
    probe.eq("先有一条", 1, 1)  # 簇的条数是这一簇自己跑的,不含之前已有的
    _cluster(probe, "簇甲", quiet, 7)
    _cluster(probe, "簇丙", idle)
    rec.eq("簇16 _cluster 正常跑完:转交额外参数、记下函数名与各自的条数", (probe.checks, probe.failures, probe.clusters), (3, [], [("quiet", 2), ("idle", 0)]))

    def inner():
        raise ValueError("坏了")

    def boom(r):
        r.eq("先通过", 1, 1)
        inner()  # 异常从更里层的函数抛出:记下的应是最里层的那个函数

    probe = _Recorder()
    _cluster(probe, "簇乙", boom)
    rec.eq("簇16 _cluster 把异常记成一条失败,并记下函数名与条数", (probe.checks, len(probe.failures), probe.clusters), (2, 1, [("boom", 2)]))
    name, expected, actual = probe.failures[0]
    rec.eq("簇16 异常失败的名字带簇名与上一条已完成的对照", name, "簇乙 运行中抛出异常(上一条已完成的对照:先通过)")
    rec.eq("簇16 异常失败的期望是不抛异常", expected, "不抛异常")
    rec.eq(
        "簇16 异常失败的实际带异常类型、信息与抛出处(文件名:行号 最里层的函数名)",
        re.fullmatch(r"ValueError: 坏了 @ [^:\s]+\.py:\d+ inner", actual) is not None,
        True,
    )
    # _cluster_gaps:每个应跑的簇都要出现过、且至少一条对照
    rec.eq("簇16 簇都跑了:没有缺口", _cluster_gaps([("a", 2), ("b", 1)], ["a", "b"]), [])
    rec.eq("簇16 有簇没被调用:报它", _cluster_gaps([("a", 2)], ["a", "b"]), ["b"])
    rec.eq("簇16 有簇跑了但一条对照都没有:报它", _cluster_gaps([("a", 2), ("b", 0)], ["a", "b"]), ["b"])
    rec.eq("簇16 缺口按名字排序", _cluster_gaps([("b", 1), ("a", 1)], ["b", "d", "a", "c"]), ["c", "d"])
    rec.eq("簇16 跑了却不在应跑列表里:报它", _cluster_gaps([("a", 2), ("x", 1)], ["a"]), ["x"])
    rec.eq("簇16 应跑列表是空的、而实际跑了簇:都报出来(发现表达式失灵时就是这样)", _cluster_gaps([("a", 1), ("b", 1)], []), ["a", "b"])


def _selftest_cli(rec):
    """簇 17:命令行入口 main()——退出码与输出流:通过时 stdout 一行 PASS;有问题时 stderr 逐条 FAIL:、退出码 1;帮助;参数个数不对;--selftest 的分派。"""
    import builtins  # 放在函数里:文件头一行都不动
    import contextlib
    import io

    def run(argv):
        out, err, saved = io.StringIO(), io.StringIO(), sys.argv
        sys.argv = ["check-unity-chain.py"] + argv
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = main()
        finally:
            sys.argv = saved
        return code, out.getvalue(), err.getvalue()

    passed = f"PASS: timescale={_PIN_CALLS} 处均带 -dnfquitaftersim, 通过判据 {_PIN_CRITERIA} 行与基线逐行一致\n"
    rec.eq("簇17 不带参数:检查默认脚本,通过", run([]), (0, passed, ""))
    rec.eq("簇17 指定真实脚本的路径:通过", run([DEFAULT_SCRIPT]), (0, passed, ""))
    usage = __doc__ + "\n"
    rec.eq("簇17 -h:打印用法、退出码 0", run(["-h"]), (0, usage, ""))
    rec.eq("簇17 --help:打印用法、退出码 0", run(["--help"]), (0, usage, ""))
    rec.eq("簇17 两个参数:打印用法、退出码 2", run(["a", "b"]), (2, usage, ""))
    rec.eq("簇17 三个参数:打印用法、退出码 2", run(["a", "b", "c"]), (2, usage, ""))
    rec.eq("簇17 -h 后面还跟了参数:是参数个数不对,退出码 2", run(["-h", "x"]), (2, usage, ""))
    # --selftest 的分派:换掉 run_selftest(不然会递归地把整套自测再跑一遍),只认恰好一个参数 --selftest
    real_selftest = globals()["run_selftest"]
    globals()["run_selftest"] = lambda: 99
    try:
        dispatched = run(["--selftest"])
        not_dispatched = run(["--selftest", "x"])
    finally:
        globals()["run_selftest"] = real_selftest
    rec.eq("簇17 --selftest:交给 run_selftest、自己不另外输出", dispatched, (99, "", ""))
    rec.eq("簇17 --selftest 后面还跟了参数:不分派,是参数个数不对、退出码 2", not_dispatched, (2, usage, ""))
    # 指定一个磁盘上不存在的路径,内容由补丁式的 open 供给:不碰文件系统(不依赖临时目录可写,超时被杀也不留残渣)
    fake = os.path.join(ROOT, "tools", "synthetic-selftest.sh")
    real_open = builtins.open
    opened = []

    def fake_open(path, *args, **kwargs):
        if path == fake:
            opened.append(kwargs.get("encoding"))
            return io.StringIO(_CALL + "\n")  # 缺 -dnfquitaftersim,而且没有任何判据行
        return real_open(path, *args, **kwargs)

    saved_cwd = os.getcwd()
    os.chdir(os.path.join(ROOT, "tools"))  # 信息里的文件名要相对仓库根,不能随当前目录变
    builtins.open = fake_open
    try:
        got = run([fake])
    finally:
        builtins.open = real_open
        os.chdir(saved_cwd)
    want_err = (
        "FAIL: tools/synthetic-selftest.sh:1: 带 -dnftimescale 的 $app 调用缺少正数 -dnfquitaftersim 参数\n"
        f"FAIL: tools/synthetic-selftest.sh: 通过判据行与基线不一致 (基线 {_PIN_CRITERIA} 行, 实际 0 行, 首个差异下标 0):\n"
        f"  期望: {_FIRST_CRITERION}\n"
        "  实际: <EOF>\n"
    )
    rec.eq("簇17 指定有问题的文件:stdout 空、stderr 逐条 FAIL:(文件名相对仓库根)、退出码 1", got, (1, "", want_err))
    rec.eq("簇17 读被检文件时指定 UTF-8 编码(与当前区域设置无关)", opened, ["utf-8"])


def run_selftest() -> int:
    with open(DEFAULT_SCRIPT, encoding="utf-8") as f:
        valid_text = f.read()

    # 1. Positive control: current unity-build-town.sh passes
    errs, ts_cnt, crit_cnt = check_text(valid_text, "<selftest-valid>")
    if errs:
        print("自测失败(正对照未通过):", errs, file=sys.stderr)
        return 1

    # 2. Negative control A: single-line -dnftimescale missing -dnfquitaftersim
    missing_sim_single = re.sub(
        r"(-dnfsave\s+\"\$save_file\".*?-dnftimescale\s+\S+)\s+-dnfquitaftersim\s+\S+",
        r"\1",
        valid_text,
        count=1,
    )
    assert missing_sim_single != valid_text
    errs_a, _, _ = check_text(missing_sim_single, "<selftest-missing-sim-single>")
    if not errs_a:
        print("自测失败(负对照 A: 单行缺少 -dnfquitaftersim 却通过了)", file=sys.stderr)
        return 1

    # 3. Negative control B: multi-line -dnftimescale missing -dnfquitaftersim
    missing_sim_multi = re.sub(
        r"(-dnfflowchoices\s+retry,select,town.*?\\\n\s+-dnftimescale\s+\S+)\s+-dnfquitaftersim\s+\S+",
        r"\1",
        valid_text,
        count=1,
        flags=re.DOTALL,
    )
    assert missing_sim_multi != valid_text
    errs_b, _, _ = check_text(missing_sim_multi, "<selftest-missing-sim-multi>")
    if not errs_b:
        print("自测失败(负对照 B: 多行缺少 -dnfquitaftersim 却通过了)", file=sys.stderr)
        return 1

    # 4. Negative control C: altered pass criteria (threshold/regex weakened)
    altered_crit = valid_text.replace("botDone=True choicesMade=3", "botDone=True choicesMade=1", 1)
    errs_c, _, _ = check_text(altered_crit, "<selftest-altered-crit>")
    if not errs_c:
        print("自测失败(负对照 C: 篡改通过判据却通过了)", file=sys.stderr)
        return 1

    # 5. Negative control D: deleted check_clear_log.py line
    deleted_crit = valid_text.replace("python3 tools/py/check_clear_log.py \"$clear_log\" 1 \\", "# removed", 1)
    errs_d, _, _ = check_text(deleted_crit, "<selftest-deleted-crit>")
    if not errs_d:
        print("自测失败(负对照 D: 删除 check_clear_log.py 判据却通过了)", file=sys.stderr)
        return 1

    # 6. 细粒度对照(簇 1 至 8):边界与分支逐条钉住;失败时逐条列出名字、期望与实际
    rec = _Recorder()
    _cluster(rec, "簇1", _selftest_positive_sim)
    _cluster(rec, "簇2", _selftest_no_call, valid_text)
    _cluster(rec, "簇3", _selftest_not_a_call)
    _cluster(rec, "簇4", _selftest_every_call, valid_text)
    _cluster(rec, "簇5", _selftest_criteria_rules, valid_text)
    _cluster(rec, "簇6", _selftest_join_lines)
    _cluster(rec, "簇7", _selftest_result_contents, valid_text)
    _cluster(rec, "簇8", _selftest_verify_section, valid_text)
    _cluster(rec, "簇9", _selftest_lexer)
    _cluster(rec, "簇10", _selftest_comments)
    _cluster(rec, "簇11", _selftest_segments)
    _cluster(rec, "簇12", _selftest_multi_call)
    _cluster(rec, "簇13", _selftest_spellings)
    _cluster(rec, "簇14", _selftest_fail_loud)
    _cluster(rec, "簇15", _selftest_real_variants, valid_text)
    _cluster(rec, "簇16", _selftest_harness)
    _cluster(rec, "簇17", _selftest_cli)
    if rec.failures:
        print(f"自测失败(细粒度对照 {rec.checks} 条中有 {len(rec.failures)} 条不符):", file=sys.stderr)
        for name, expected, actual in rec.failures:
            print(f"  - {name}\n      期望: {expected!r}\n      实际: {actual!r}", file=sys.stderr)
        return 1
    # 每个 _selftest_ 开头的簇函数都必须被调用过、且至少跑了一条对照;反过来跑过的也都得在发现表里(发现表空了就不能悄悄通过):
    # 少调用一个簇、簇什么都没检查、或发现簇的表达式失灵,都不能悄悄通过
    gaps = _cluster_gaps(rec.clusters, [n for n, v in globals().items() if n.startswith("_selftest_") and callable(v)])
    if gaps:
        print(f"自测失败(簇函数与簇调用对不上:没被调用、一条对照都没跑、或不在 _selftest_ 发现表里:{gaps})", file=sys.stderr)
        return 1

    print(
        f"自测通过: 正对照通过(timescale={ts_cnt}, criteria={crit_cnt}), 4 项整脚本负对照均正确拦截, "
        f"另有 {rec.checks} 条细粒度对照全部符合预期"
    )
    return 0


def main() -> int:
    if len(sys.argv) == 2 and sys.argv[1] == "--selftest":
        return run_selftest()
    target = sys.argv[1] if len(sys.argv) == 2 else DEFAULT_SCRIPT
    if len(sys.argv) > 2 or target in ("-h", "--help"):
        print(__doc__)
        return 0 if target in ("-h", "--help") else 2
    with open(target, encoding="utf-8") as f:
        text = f.read()
    errs, ts_cnt, crit_cnt = check_text(text, os.path.relpath(target, ROOT))
    if errs:
        for e in errs:
            print("FAIL:", e, file=sys.stderr)
        return 1
    print(f"PASS: timescale={ts_cnt} 处均带 -dnfquitaftersim, 通过判据 {crit_cnt} 行与基线逐行一致")
    return 0


if __name__ == "__main__":
    sys.exit(main())
