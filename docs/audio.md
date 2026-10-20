# 声音(M8,E-089、E-090)

战斗里的声音:挥剑、命中、角色语音、地图背景音乐与环境声。本文只记**数据事实、触发规则与实现结构**;声音文件本身不入库,运行时从用户自备的数据目录里读。

证据等级:数据格式与覆盖率是对用户自备数据的直接统计(高);触发点来自客户端代码的静态读取(中高);**时机、概率、循环间隔、混音比例没有原版录音可对照**,是推断或占位(下面每处都标出)。基线录像里没有声音,所以"听起来对不对"没有任何自动检查能代替人耳。

## 1. 数据

| 部分 | 内容 |
|---|---|
| `audio.xml` | 根元素 `AudioTagDatabase`:声音标签(ID)→ 文件或一组子项。5 种条目:Effect(单个文件)、Random(按权重抽一个子项)、Group(子项各带延迟,一起放)、Ambient(环境声)、Music(背景音乐)。属性:`FILE`、`LOOP_DELAY`、`LOOP_DELAY_RANGE`、`DELAY`、`DELAY_RANGE`、子项的 `TAG`/`PROB`/`DELAY`;`VOLUME_ADJUST`、`PRIORITY` 在这份数据里恒为 0,忽略 |
| `SoundPacks/*.npk` | 每个条目就是一整个 Ogg Vorbis 文件,原样存放;条目名是 `sounds/...`,小写 |
| `Music/` | 散装的 Ogg 文件(背景音乐) |
| 文件名 | `audio.xml` 里用反斜杠、大小写混用:统一成正斜杠、不分大小写查找(`SoundLibrary.Normalize`) |

这份数据里的统计(`dnf sound-check`):

- 条目 12,405 个(Ambient 54、Effect 10,193、Group 53、Music 258、Random 1,847);ID 重复 39 个(不分大小写),**取先出现的**(客户端的重复处理没读,这是约定);没有不认识的元素。
- 文件来源:SoundPacks 10,386 项、Music 目录 240 个。带文件的条目 10,505 个:文件存在 9,865(全部是合格的 Ogg 头)、缺失 640(例如 `SW_1ATK_01` 一族)。
- Random/Group 条目 1,900 个:展开后文件全在的 1,758、只在一部分的 8、一个也没有的 134。包里与 Music 里没被任何条目引用的文件 865 个(共 10,626)。

### 脚本里的声音引用

普查(`re/notes/sound_tag_census.tsv`,本机)里带声音的脚本标签,按"有该标签的文件数"排在前面的:`.equ [move wav]`(67,875)、`.stk [move wav]`、`.atk [hit wav]`(5,410)、`.act [SOUND]` 块(5,057 个文件,17,495 个值)、`.map [sound]`(2,884)、`.mob` 的五种声音标签、`.chr [weapon wav]`(11)、`.ani` 帧上的声音(187,365 个 .ani 里有 1,316 个;主角的只出现在特殊技能动画里)。

已接入游戏的:

| 标签 | 含义 | 这份数据里能不能在 `audio.xml` 里找到 |
|---|---|---|
| `.map [sound]` | 一张地图的声音列表:音乐、环境声、周期音效混在一起(位置不固定,按 `audio.xml` 里的种类区分) | 2,948 张地图里 2,881 张有;引用 5,719 次,不同标签 310 个;找不到 540 次(35 个标签) |
| `.mob [appear sound]` `[damage sound]` `[die sound]` `[attack sound]` `[ambient sound]` | 怪物的出场、受击、死亡、出招、环境叫声 | 引用数 398 / 1,057 / 1,007 / 193 / 355 次;各有少量标签找不到(几次到几十次) |
| `.atk [hit wav]` | 攻击的命中声。**同一个文件里写了两次以上的有 41 个,加载器取最后一个** | 6,394 个 .atk 里 5,021 个有;找不到 227 次(33 个标签) |
| `.chr [weapon wav]` | 武器的四个声音:挥击 A(Random)、挥击 B、命中 A(Random)、命中 B;下标是装备的 `[sub type]` | 11 个 .chr、66 块(21 块是空的);引用 130 次,**全部能解析** |

没接入的:`.act [SOUND]`(怪物与特效在动作的某一帧放的声音,很可能才是怪物出招声的真正来源)、`.act [PLAY SOUND]` 与 `[SOUND PROBABILITY]`、`.equ [move wav]` 与 `.stk [move wav]`(移动声)、`.ani` 帧声音(主角只有特殊技能的动画带)、`.obj [sound category]`、各种 speech(语音对白)、`.ptl [start sound]`、界面按钮声。

### 客户端里写死的标签(静态读码)

鬼剑士的语音与技能音效在客户端里是一张键串表(启动时由一个大的初始化函数,起点 `0x014fee40`,逐个构造成全局字符串对象),状态函数按状态取键。**16 个键在 `audio.xml` 里全都有**:

- 语音:`R_SM_ATK`(Random,普攻喊声)、`R_SM_JUMP_ATK`(Random,跳跃攻击)、`R_SM_DMG`(Random,受击)、`SM_DIE`、`SM_JUMP`、`SM_BACKSTEP`、`SM_THRUST`、`SM_NOMANA`、`SM_COOLTIME`、`SM_COMMAND_PET`、`SM_THROW`。
- 技能与动作音效:`UPPER_SLASH_02`(上挑状态每次施放一次)、`JUMP_START`(起跳)、`JUMP_END`(落地)、`AIR_SWORD`。
- 界面:`COOLTIME_SKILL_END`(冷却完成的提示音;**触发条件没读到**,实现里用"技能槽从冷却变成可用")。

读到这些的状态函数区间(都是区间起点附近,不是函数入口):跳跃开始约 `0x00837a90`、跳跃落地约 `0x00838380`、上挑约 `0x00cbc860`、冷却完成约 `0x00b62200`。

## 2. 触发规则(`Dnf.Play` 里的 `SoundCue`)

游戏逻辑只往队列里放"提示":(标签、种类、主人)。**不碰任何音频接口**,所以整套规则可以在没有声卡的环境里测试。

| 事件 | 提示 | 依据 |
|---|---|---|
| 进房间 | `RoomEnter`(地图路径)+ 该地图 `[sound]` 里的每个标签(`RoomSound`);第一次进还有怪物的出场声(语音,主人 = 怪物序号 + 1) | 地图标签是观察;"进房间时一次性发"是实现约定 |
| 普攻出手 | 语音 `R_SM_ATK` + 武器挥击声(`[weapon wav]` 的 A,连招最后一段用 B) | 键是观察;**A/B 对应连招段是推断** |
| 奔跑攻击 / 跳跃攻击 | `SM_THRUST` + 挥击 A / `R_SM_JUMP_ATK` + 挥击 A | 键是观察;配对是推断 |
| 起跳 / 落地 | `JUMP_START` + `SM_JUMP`(后跳是 `SM_BACKSTEP`)/ `JUMP_END` | 观察 |
| 上挑 | `UPPER_SLASH_02` | 观察 |
| 技能没放出来 | 魔法不够 `SM_NOMANA`、冷却中 `SM_COOLTIME`(语音) | 键是观察;触发条件是推断 |
| 技能冷却完成 | 界面音 `COOLTIME_SKILL_END` | 键是观察,**触发条件是推断** |
| 打中怪物 | 命中声:攻击自己的 `[hit wav]`,没有就用武器命中声(A,连招最后一段 B);怪物受击 / 死亡语音(`.mob`) | `[hit wav]` 是观察;**武器命中声的回退与 A/B 是推断**(主角普攻的 .atk 没有 `[hit wav]`) |
| 怪物出招 | 怪物的出招语音(每次新的挥击一次) | `.mob [attack sound]` 是观察;**触发时刻是近似**(真正的来源很可能是 `.act [SOUND]`,没做) |
| 主角被打 | 攻击的命中声(Effect)+ `R_SM_DMG`,死亡 `SM_DIE`(语音) | 键是观察 |

语音有**主人**:主角是 0,怪物是它在房间里的序号加 1。

## 3. 调度器(`SoundScheduler`)

把提示变成"放哪个文件、什么时候停"的命令(`Play`/`Stop` + 句柄),同样不碰音频接口:

- **音效、界面音**:按 `audio.xml` 展开(Random 按权重抽、Group 逐项带延迟);同一个标签 30 毫秒内只放一次(同一击同时打中两只怪不叠两遍,这个窗口是我定的)。
- **语音**:同一个主人的新语音打断它上一个还没放完的;不同主人互不影响。
- **房间声音**:`RoomEnter` 之后的 `RoomSound` 是这个房间的全部地图声音。音乐只在换了一首时才换(旧的淡出);房间里没有音乐就**沿用当前的**(推断);环境声与周期音效跟着房间走:两个房间都有的不重放、只有旧房间有的停掉、只有新房间有的开始。
- **循环**(`LOOP_DELAY` 的含义是推断):没写 = 只放一遍;`0` = 连续循环;`N` > 0 = 每次放完隔 N 秒(加随机范围,重新抽)再放,**第一次也隔一个间隔**(免得一进房间所有周期音效同时响)。
- 延迟(`DELAY`)、间隔的单位按秒,同样是推断。

## 4. Unity 播放(`DnfAudio`,`com.dnfunity.client`)

- Unity 只能从文件解码 Ogg:包里的条目先写到临时目录(`Application.temporaryCachePath/dnf-audio`),用 `UnityWebRequestMultimedia` 读成 `AudioClip`(整个读进内存、保持压缩,播放时再解码),读完删临时文件;`Music` 目录里的散装文件直接从原路径读。读取走 `file://` 地址(只在 macOS 验证过,其他三个平台的路径与解码没有验证)。
- 读文件在协程里做,**第一次用到某个声音要晚几帧才响**:晚了超过 0.35 秒的音效、语音、界面音直接放弃(和画面对不上);音乐与房间环境声不放弃。
- `AudioSource` 池(最多 48 个),每个通道有同时放的个数上限;换歌淡出 0.6 秒;演示暂停时所有声音一起暂停;剪辑缓存最多 400 个(丢最久没用的)。
- **混音比例、个数上限、淡出时长都是占位**(原版的混音表没读)。
- 命令行:`-dnfnosound` 完全不开声音;`-batchmode` 的无人值守运行默认把总音量压成 0(整条链路照跑、计数器照记,只是不出声),`-dnfsound` 才出声;离屏渲染一帧(`-dnfshot`)不开声音,所以对拍不受影响。数据目录里没有 `audio.xml` 时安静地没有声音。
- 日志行:`DNF_AUDIO ready tags=… packed=… loose=… audible=…`,结束时 `DNF_AUDIO cues=… played=… started=… stopped=… coalesced=… unknown=… loaded=… failed=… late=… stolen=… inFlight=… maxInFlight=… clips=… roomSounds=… music=…`。

## 5. 怎么验证

```
dotnet test DnfUnity.slnx -c Release                    # 合成数据:audio.xml 解析与展开、提示发出、调度器、SoundLibrary
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll sound-check [--bot [--dungeon …] [--seconds N] [--seed N] [--no-damage]] [--json <文件>]
tools/unity-test.sh EditMode                            # AudioDecodeTests:真实 Ogg(包里的、散装的各一个)能被 Unity 解成 AudioClip
tools/unity-build-town.sh macos                         # 实时整局的 DNF_AUDIO 计数器:收到过提示、开始放过、读成功过、没有读失败
tools/play-dungeon.sh [bot|explore] [nodamage]          # 自己听(窗口运行,默认出声)
```

`sound-check --bot` 让自动机打一局,收集全部提示,检查每个提示的标签能不能解析到存在的文件:这份数据里**覆盖率 100%**(没有解析不到的提示);Unity 实时整局收到的提示总数与命令行同一局(同种子)一致。

## 6. 没做 / 没验证

- **没有人耳验证**:我在无人值守的环境里只能检查数据、触发和读取的计数,没有听过实际输出。
- 怪物出招声改读 `.act [SOUND]`;`.mob [ambient sound]` 的节奏;移动声(`[move wav]`);界面按钮声;`.ani` 帧声音(特殊技能)。
- 客户端真实的语音概率、A/B 选择规则、命中声回退、循环间隔的语义:没有录音,也没有读到调度代码。
- 3D 定位:没有做(全部当作 2D 音源)。声像与距离衰减原版有没有,没查。
- Windows、Android、iOS 上的解码与播放没有验证(只在 macOS 编辑器与播放器里验证)。
