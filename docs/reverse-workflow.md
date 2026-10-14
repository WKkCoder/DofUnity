# 逆向工作流(DNF.exe 静态分析)

范围:用户已同意对 `DNF.exe` 做**只读的静态反汇编与反编译**,以及(2026-10-14 起)**只做静态的字符串解码**(客户端自己的文字块,E-041;用途限于还原 `sq_*` 注册名与客户端专用标签名)。不运行它,不脱壳,不解密保护层,不碰 `TerSafe`;如果某个问题只有脱壳或解密才能回答,就停下来问用户。静态能读到多少、哪些区域被混淆或加密见 E-044,那些区域在得到许可前不碰。

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
| `FixSwitches.java` | 补全没解出的跳转表(`apply` 才写入;只在 `dnffix` 上用) |
| `RefsIntoRegions.java`、`SampleUndefinedRefs.java`、`RefsFrom.java` | 看引用落在哪里、取样没反汇编的字节 |
| `Peek.java`、`Xrefs.java`、`TagRefs.java`、`P6Survey.java` | 早先的小工具 |

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
9. 受保护区域(`nsn#3` 的后半、`#6`、`#7`,约 13 MB,E-044):是否值得读、怎么读,**先问用户**;不做运行时转储与脱壳,除非用户明确同意。
