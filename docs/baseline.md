# 对照基线:原版客户端怎么跑、怎么截、截什么

原版客户端是整个项目的对照标准:Unity 里复刻出来的画面、数值与行为,最终都要和它比。这份文件只写**可重复的做法与场景定义**。截图、录屏、日志本身只放在 `re/baseline/`(不入库,提交守卫会拦),这里不放任何从原版里取出的素材,也不放账号、密码、服务器地址。

## 环境(2026-10-14 首次跑通)

| 项 | 值 |
|---|---|
| 主机 | macOS 15.7(arm64,Apple M4 Pro) |
| 运行层 | CrossOver 26.1,瓶子 `DNF`(`~/Library/Application Support/CrossOver/Bottles/DNF`) |
| 客户端 | 本机游戏目录里的 `DNF.exe`(SHA-256 `d422613f…a0fe`,与 `.bak_original` 只差 1 个字节,见 E-012)与启动器 `dnf-launcher.exe`(SHA-256 `cb5f4ba0…7a47`) |
| 数据 | `Script.pvf`(SHA-256 `fb012d6f…832d0`)与 `ImagePacks2`;客户端与服务端的 PVF 不是同一份(E-016) |
| 服务器 | 用户自己的服务器;启动器走 HTTP 取令牌,`ijl15.dll` 把 `start.dnf.tw` 指到用户的服务器(地址只在 `local.secrets.env`) |
| 启动环境 | `WINEDLLOVERRIDES=ijl15=n,b`、`WGPU_BACKEND=vulkan`、`WINE_D3D_CONFIG=csmt=0`、`DNF_PLUGIN_ENABLED=0`(插件注入关闭) |
| 画面 | 游戏内容 800×600;macOS 窗口 400×300 点,2 倍屏下截出来是 800×600 像素 |
| 运行中的第三方 | 用户自己的 `DNFScale.app`(等比例小窗)在运行,可能改变窗口大小;每次采集都记录实际窗口尺寸 |

首次运行的实测结果:启动器自动用已记住的账号取令牌并启动 `DNF.exe`;代理 DLL 的日志确认域名被指向用户的服务器;登录、选角之后进入城镇(`02-client-window.png`:城镇 HUD、NPC、传送装置、聊天栏都正常出现)。用户点了"进入游戏",之后选角等操作也是用户手动完成的(我没有键鼠输入权限)。

## 做法

1. 启动器:`tools/baseline/launch-dnf.sh`(要放在后台跑,它会一直占着终端)。它不包含 DNF-CX 脚本里的试用期处理,也不改注册表。
2. 在启动器窗口点"进入游戏"。账号已被启动器记住;凭据不经过我这边的任何命令行。
3. 采集:`tools/baseline/tool.sh shot --owner DNF.exe --title "DNF Taiwan" --out re/baseline/runs/<运行目录>/shots/<场景>-<序号>.png`。
   - 小工具是一个只认窗口所有者名含 `wine`、`dnf`、`crossover` 的最小权限助手(`tools/baseline/DnfBaselineTool.swift`),不会截到别的程序。
   - `tool.sh windows` 列出可截的窗口;`tool.sh status` 查看授权状态。
4. 收尾:关游戏窗口,再关启动器;`ps -axo pid,etime,comm` 确认没有残留的 `wineserver`、`DNF.exe`、`dnf-launcher.exe`。

授权现状与 macOS 15 + Wine 实测机制(2026-10-16,证据见 E-064):

- **本机固定代码签名消除重建后的重复授权**:`codesign --sign -`(临时签名)把 TCC 授权绑定在每次编译都会变化的 `cdhash` 上。`tools/baseline/make-signing-identity.sh` 用系统 `/usr/bin/openssl` 生成自签名代码签名证书 `DnfBaselineTool Local Signing`(保存在不入库的 `re/baseline/signing/dnfbaselinetool.keychain-db`),`build-tool.sh` 编译时自动用该证书签名,产出的指定要求是 `identifier "local.dnfunity.baselinetool" and certificate leaf = H"0ca487a7d2284638fd00919859e9f7dcc8ba98f4"`。实测重新编译后 `screen_capture_access=true` 与 `accessibility_trusted=true` 直接保持有效,不再需要去系统设置里删掉重加。
- **前台应用检测**:不跑 AppKit 主循环的命令行进程里,`NSWorkspace.shared.frontmostApplication` 会停留在启动时的旧值;改调 `/usr/bin/lsappinfo front` 与 `lsappinfo info -only pid` 读取 WindowServer 的真实前台进程。
- **把 Wine 窗口切到前台**:macOS 15 拒绝后台进程的 `NSRunningApplication.activate`,而对 Wine 进程(`dnf-launcher.exe`、`DNF.exe`)调 `AXUIElementSetAttributeValue(kAXFrontmostAttribute)` 与 `kAXRaiseAction` 虽然返回 `.success`(0),窗口并不会真正激活。实测可靠的做法是:先用 `CGWindowListCopyWindowInfo([.optionOnScreenOnly])` 检查目标窗口安全点没有被别的窗口遮挡(`topWindowAt(p)?.id == w.id`,忽略 `alpha < 0.05` 的透明层以及距离屏幕边缘 110 点以内的 `layer=20` 程序坞全屏透明层),再向该点发一次 HID 鼠标点击(`cghidEventTap`)。有标题栏的窗口点标题栏(`y + 12`),无边框的 `DNF.exe` 用 `--front-point 280,6`(顶部空白黑条)。
- **输入送达与 100 毫秒连拍(`burst`)**:`DNF.exe` 在前台时,进程级键盘事件(`CGEvent.postToPid`)对游戏内移动、普攻(`x`)、跳跃(`c`)、技能(`a`/`s`/`d`)、空格与 `esc` 全部生效;游戏内 UI 按钮(频道选择、"游戏开始"、地图入口)需要 `--hid` 鼠标点击。用户运行中的 `DNFScale.app` 把 `DNF.exe` 窗口缩为 `400×300` 点,因此窗口内点击坐标是 `800×600` 逻辑坐标的一半,而 `ScreenCaptureKit`(`SCScreenshotManager.captureImage`)直接截出原生 `800×600` 像素画面。`burst` 子命令在同一个进程内启动按键线程并按固定间隔连拍(实测 100 毫秒间隔下 `avg_interval=100ms`、`max_gap=103..105ms`),每帧起止时间写入 `index.tsv`。
- **服务端频道进程懒启动**:首次点选频道进入角色选择画面时,服务端会按需拉起对应频道的 `df_game_r` 进程,在约 90 秒内占 ~55% CPU 加载 `Script.pvf`(~576 MB RSS)。若在 `df_game_r` 加载完之前点"游戏开始",客户端会提示"数据连接中断";等 `df_game_r` 的 CPU 降到 0% 后再点"游戏开始",即可秒进赛丽亚房间。
- **屏幕锁定时不要做 GUI 自动化**:游戏窗口被锁屏遮住时截图拿到的是过期画面。自动化前先跑 `tools/baseline/session-check.sh`,报告 `screen_locked=true` 时停手。

## 场景清单(2026-10-16 全部采齐,证据见 E-064)

状态里的"已采"指本机 `re/baseline/runs/20261016-092839-b2/`(及首轮 `20261014-145210/`)下已有对应截图与 `index.tsv` 时间轴。

| 编号 | 场景 | 怎么到达 | 采什么 | 用于 | 状态 |
|---|---|---|---|---|---|
| S01 | 启动器与服务器选择 | 启动器按回车后等客户端出窗口 | 启动器整窗、服务器选择画面、频道选择画面 | P7(登录流程) | 已采(`S01-launcher-b2`、`S01-win-00..02-after-start`) |
| S02 | 角色选择 | 选频道进入角色列表 | 整窗截图 | P7、P8 | 已采(`S02-select-char1.png`) |
| S03 | 城镇静止 | 进入赛丽亚房间与艾尔文防线站定 | 100 毫秒连拍 30 张 + 1 秒连拍 11 张 | P3(背景分层、NPC、UI 位置与待机帧率) | 已采(`S03-idle100/`、`S03-idle1s/`、`S05-town2-00.png` 及首轮赫顿玛尔/素喃) |
| S04 | 城镇行走 | 赛丽亚房间与艾尔文防线行走 | 100 毫秒连拍 40 张 + 向右走到副本口 | P3(镜头跟随、角色行走动画) | 已采(`S04-walk/S04-000..039.png`、`S05-walk-right-1..2.png`) |
| S05 | 副本选择界面 | 走到洛兰入口进选图画面 | 整窗截图 | P8 | 已采(`S05-dungeon-select.png`、`S05-map-again.png`) |
| S06 | 副本房间待机 | 进洛兰首房间过渡与站定 | 进图过渡 50 张 + 房间待机 30 张(100 毫秒间隔) | P3、P5 | 已采(`S06-enter-burst/`、`S06-room1-00.png`、`S06-room-idle/`) |
| S07 | 普通攻击连击 | 连按 `x` 打出三段普攻连击 | 100 毫秒连拍 30 张(含命中数字与血条) | P3(`.ani` 逐帧对照)、P5 | 已采(`S07-attack/S07-000..029.png`) |
| S08 | 单个技能释放 | 放固定技能(`a` 鬼斩、`s` 上挑、`d` 后跳;键位对应关系由 HUD 快捷槽图标逐像素比对得出并经动作验证,证据 E-076,更正了此前写的"`s` 刀魂之卡赞、`d` 上挑") | 100 毫秒连拍 30 张 + 40 张 | P4、P5 | 已采(`S08-skill/S08-000..029.png`、`S08b-skill-s/S08b-000..039.png`) |
| S09 | 怪物受击与死亡 | 打死哥布林并拍 AI 接近 | 100 毫秒连拍 70 张(受击、血条归零、死亡闪白、掉落、开门)+ AI 40 张 | P5(伤害、硬直、死亡与房门) | 已采(`S09-kill/S09-000..069.png`、`S09b-ai/S09b-000..039.png`) |
| S10 | 副本结算 | 进洛兰领主房击杀领主并通关 | 100 毫秒连拍:进领主房 60 张 + 领主战 2×80 张(含击杀闪光与通关提示)+ 结算界面 80 张(篝火、德利拉、掉落、继续菜单) | P5、P8 | 已采领主战、击杀瞬间、通关后菜单与掉落(`S10-boss-enter2/`、`S10-boss-fight1..2/`、`S10-settlement/`);**没采到结算评分面板**:击杀末帧与菜单首帧之间有 38.5 秒空档(见 E-064 第 6 点),需要补录 |

每个场景的设置与文件路径记在 `re/baseline/runs/20261016-092839-b2/scenes.txt`,网格缩略图合成工具为 `tools/baseline/contact_sheet.py`。

## 还没定的事

- **差分阈值**:P3 开始前约定。逐像素比不现实(缩放、字体、特效随机),计划用"关键点位置 + 感知哈希 + 人工抽查"三道。
- **时间轴**:现在只有静态截图,没有逐帧时间。要对 `.ani` 的延时,就得做定时连拍;屏幕录制授权已够用。
- **固定输入序列**:`record.py` 已能按脚本发输入并记时间,但小工具每次调用要经 `open` 启动一次(约 0.26 秒),做不了 100 毫秒级的连拍,也切不了前台。要对 `.ani` 的帧延时和战斗时序,得做一个把"切前台 + 定时输入 + 窗口连拍"放在同一个进程里的运行器;为了不用每次重建都重新授权,运行器做成通用的场景解释器(场景脚本在运行时读取,二进制不再改)。
- **城镇里的聊天栏、公告、头像**含用户的角色名与服务器内容:截图只在本机留存,文档里不引用。
