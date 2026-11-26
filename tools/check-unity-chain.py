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


def check_text(text: str, label: str = "tools/unity-build-town.sh"):
    errors = []
    lines = text.splitlines()

    # (a) Every $app invocation with -dnftimescale must also have -dnfquitaftersim <positive>
    timescale_count = 0
    for start_ln, cmd in join_logical_lines(lines):
        if cmd.startswith("#") or '"$app"' not in cmd:
            continue
        tokens = cmd.split()
        if "-dnftimescale" in tokens:
            timescale_count += 1
            m = re.search(r"(?:^|\s)-dnfquitaftersim\s+([0-9]+(?:\.[0-9]+)?)(?:\s|$)", cmd)
            if not m or float(m.group(1)) <= 0.0:
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
# 其中 case "$cli_hero" 与 case "$saved_fields" 是冗余项(含它们的行必然也含更短的 cli_hero / saved_fields),
# 删掉或改动这两项在任何输入下输出都相同;其余每一项都有各自独立的对照。
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

    def eq(self, name, actual, expected):
        self.checks += 1
        self.last = name
        if actual != expected:
            self.failures.append((name, expected, actual))


def _cluster(rec, name, fn, *args):
    """运行一簇对照;被测函数抛异常(缺陷或变异)时记为该簇的一条失败,不让回溯盖住其余对照。"""
    import traceback  # 只在失败路径用;放在这里,不让文件头多出一行而顺移被测函数的行号

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


def _only(errors, category):
    """按错误类别(信息里的关键词)筛选。"""
    return [e for e in errors if category in e]


def _msg_sim(line_no):
    return f"{_LABEL}:{line_no}: 带 -dnftimescale 的 $app 调用缺少正数 -dnfquitaftersim 参数"


def _msg_no_call():
    return f"{_LABEL}: 未找到任何带 -dnftimescale 的 $app 调用"


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
        ("$app 只有右引号", 'run $app" -dnftimescale 5\n' + ok),
        ("$app 只有左引号", 'run "$app -dnftimescale 5\n' + ok),
    )
    for name, text in cases:
        errs, calls, _ = check_text(text, _LABEL)
        rec.eq(f"簇3 {name}不算调用", (_only(errs, _CAT_SIM), calls), ([], 1))
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
    if rec.failures:
        print(f"自测失败(细粒度对照 {rec.checks} 条中有 {len(rec.failures)} 条不符):", file=sys.stderr)
        for name, expected, actual in rec.failures:
            print(f"  - {name}\n      期望: {expected!r}\n      实际: {actual!r}", file=sys.stderr)
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
