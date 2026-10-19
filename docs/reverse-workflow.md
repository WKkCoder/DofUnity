# 逆向工作流(DNF.exe 静态分析)

范围:用户已同意对 `DNF.exe` 做**只读的静态反汇编与反编译**,以及(2026-10-14 起)**只做静态的字符串解码**(客户端自己的文字块,E-041;用途限于还原 `sq_*` 注册名与客户端专用标签名)。**2026-10-15 起范围扩大**:用户答复"给你最高权限 你想逆向就逆向",所以受保护区域也可以分析,并且可以在 CPU 模拟器里(不是游戏本体)运行客户端与服务端的机器码(E-059,边界见 `docs/boundaries.md` 第 3 条)。读受保护区域的守卫仍是默认开启的(`read_va.py`、`DumpFunction.java`、`InferCalleeParams.java` 默认拒绝),要读时显式设环境变量 `DNF_ALLOW_PROTECTED=1`,这样每一次越界都留下痕迹。静态能读到多少、哪些区域被混淆或加密见 E-044。

## 目录约定

- `re/`:一切逆向产物,已被 `.gitignore` 与提交守卫排除,不进仓库。
  - `re/ghidra/projects/dnf`:Ghidra 工程(导入并分析过的 `DNF.exe`),保持原样
  - `re/ghidra/projects/dnffix`:`dnf` 的副本,已用 `FixSwitches.java` 补全 28 处跳转表(E-044);**之后的分析都跑在这个副本上**,地址与 `dnf` 一致
  - `re/ghidra/logs/`:导入与分析日志
  - `re/notes/`:反编译片段、汇编清单等原始笔记(只给自己看,不提交)
- `tools/ghidra/`:自己写的 Ghidra 脚本(原创代码,可以提交)。
- `docs/evidence/ledger.md`:进仓库的只有用自己的话写的结论、地址、命令与证据 ID。

## 命令

导入并自动分析(只读取 `DNF.exe`,不改它):

```sh
set -a; . ./local.env; set +a
GHIDRA_HEADLESS_MAXMEM=8G nice -n 10 ~/Applications/ghidra_12.1.4_PUBLIC/support/analyzeHeadless \
  re/ghidra/projects dnf -import "$DNF_DIR/DNF.exe" \
  -analysisTimeoutPerFile 14400 -log re/ghidra/logs/import_analyze.log
```

对已分析的工程跑脚本(不重新分析,不保存修改):

```sh
~/Applications/ghidra_12.1.4_PUBLIC/support/analyzeHeadless re/ghidra/projects dnffix \
  -process DNF.exe -noanalysis -readOnly -scriptPath tools/ghidra -postScript <脚本名>.java <参数>
```

本仓库的 Ghidra 脚本(都只读,`FixSwitches.java` 加 `apply` 时才写工程):

| 脚本 | 用途 |
|---|---|
| `DumpFunction.java` | 导出某个函数的反汇编、反编译与调用关系 |
| `AsmRange.java` | 导出一段地址范围的反汇编,标出每条指令所属函数 |
| `StringCalls.java` | 列出字符串访问函数的所有调用点与数据块地址(E-041) |
| `FuncCoverage.java` | 统计函数覆盖与函数之间的空隙(E-044) |
| `ListFunctions.java`、`ListFunctionRefs.java` | 列出地址范围内的函数;前者只往标准输出列 `FUNC` 行并标出受保护范围,后者写 TSV 并带被引用次数——C++ 类的方法在内存里挨在一起,知道一个方法的地址后看它前后的函数,能圈出同一个类的其余方法(E-073) |
| `FindOffsetUses.java`、`FindVirtualCalls.java` | 找字段偏移被哪些指令读写、哪些调用点经过某个虚表槽(命中里要靠上下文排除别的类的同位移字段) |
| `FindImmUses.java`、`FindStateCalls.java` | 找指令里直接带某个 32 位常量的地方(例如虚表地址 → 写它的构造函数,E-073);找"切换状态"的调用点并标出压栈的状态号(状态机的入口) |
| `CreateFunctions.java` | 在没有函数的地址上建立函数(**不带 `-readOnly`**,只改 `dnffix` 工程副本;原生接口的实现常常只被函数指针引用) |
| `FixSwitches.java` | 补全没解出的跳转表(`apply` 才写入;只在 `dnffix` 上用) |
| `RefsIntoRegions.java`、`SampleUndefinedRefs.java`、`RefsFrom.java` | 看引用落在哪里、取样没反汇编的字节 |
| `Peek.java`、`Xrefs.java`、`TagRefs.java`、`P6Survey.java` | 早先的小工具 |

配套的 Python 小工具(都在 `tools/py/`,只读):`loader_stores.py` 把一个脚本加载函数的汇编清单按标签切段,列出每个标签的处理段写了对象的哪些字段(例如 `.chr` 的 `[jump power]` 落在角色对象的哪个位移,E-073);`vslots.py` 从磁盘读六个核心类的虚表槽;`read_va.py` 按虚拟地址读磁盘字节(读受保护区域要 `DNF_ALLOW_PROTECTED=1`)。

## CPU 模拟器(E-060、E-061)

用来运行客户端与服务端的机器码,看受保护区的跳板与函数做什么;只在 CPU 模拟器里跑,不运行游戏本体。环境:`python3 -m venv re/venv && re/venv/bin/pip install unicorn==2.1.4 capstone==5.0.7`(`re/` 不入库)。

| 工具 | 用途 |
|---|---|
| `tools/py/emu.py` | 模拟器本体:装载 `DNF.exe` 的节、假导入、Win32 与运行库替身、`call()`、受保护整数的辅助、`--selftest` |
| `tools/py/win32_argc.py` | Win32 接口的 stdcall 参数个数表(没登记的接口模拟器会停下报名字) |
| `tools/py/emu_stub_probe.py` | 探一个"被偷走指令"的跳板:把寄存器当标记指针,输出"寄存器 = [寄存器 + 位移]"、写入、标志位依赖 |
| `tools/py/emu_boot.py` | 客户端"开机"(C 运行库启动 + 7,738 个 C++ 全局构造),可存与读快照 `re/snap/boot.snap` |
| `tools/py/emu_stub_census.py` | 抽样跑明文里指向受保护区的调用点(结果噪声大,只作参考) |

模拟器的纪律:导入函数没登记就停,不要按 0 个参数硬跑;写进模拟内存后要清翻译缓存(`emu.write` 已做);受保护代码里每条指令都挂钩子(默认);只写 `out/` 下的实验产物。

在 zsh 里把参数放进变量再展开不会被拆开,要么逐个写出来,要么用函数传 `"$1" "$2"`。

注意:在 Shell 工具里要把长任务放进后台(`block_until_ms: 0`),否则命令结束时进程会被一起结束。

## 写进台账的格式

每条结论一行:ID、结论(观察 / 推断 / 未知)、来源(REA 带 Evidence ID,或非 REA 带可复现命令与脚本名)、置信度与未解释的部分。
函数只用地址与自拟名称,不用 Ghidra 的自动命名,也不贴反编译代码。

## 优先问题(与 P3 到 P5 并行推进)

已完成:

1. ~~反汇编覆盖率~~:`nsn#0` 87.69% 被解成指令、48,013 个函数(E-010)。
2. ~~谁引用了 `0x81A79011`~~:只有 VA `0x011AB180` 一个函数,它同时做解密与 CRC-32 校验,规则在全库上验证(E-011)。

待做:

3. 标记流脚本的读取与分发:找到把 5 字节标记转成对象的函数,它的 switch 就是标签语义的原表。入口线索:PVF 文件读取的调用者(VA `0x011ACA20` 与 `0x011ACB40` 的上游,即各类资源的加载函数,如 `0x01175620`、`0x01178150`、`0x01179CC0`)。
4. ~~Squirrel 的注册表~~:字符串解码已完成(E-041),注册函数 `0x0060B630` 已读出 699 个 `sq_` 名字与 694 个实现地址(E-042)。
5. ~~哪个函数认识哪种脚本的标签~~:已按标签集合对上(E-043);读代码核实是下一步。

待做(续):

6. 每种脚本的"标签 → 处理代码":`.ai` 先在 `0x008D10C0` 逐个登记关键字(编号 8 到 63)再按编号分发,要找到按编号分发的函数;`.act` 的 `0x01333530` 看起来是一长串字符串比较,要逐段读出每个标签读几个值、写到对象的哪个字段。
7. 原生接口的实现(`sq_registry.tsv` 里 694 个实现地址):多数是 C++ 成员函数,很短的取字段函数可以批量读出;登记调用的 339 个不同目标决定了参数编组,可以据此分组恢复签名。共用地址的别名要逐个核对(链接器会折叠相同代码)。
8. `.ani` 的播放与判定:帧延时、图形特效、判定框的使用方式。
9. 受保护区域(`nsn#3` 的后半、`#6`、`#7`,约 13 MB,E-044):用户已授权(E-059)。做法见 E-060 起:先用 CPU 模拟器跟踪被混淆的块,取它的读写与调用;运行时转储是最后手段。
