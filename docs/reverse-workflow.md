# 逆向工作流(DNF.exe 静态分析)

范围:用户已同意对 `DNF.exe` 做**只读的静态反汇编与反编译**。不运行它,不脱壳,不解密,不碰 `TerSafe`;如果某个问题只有脱壳或解密才能回答,就停下来问用户。

## 目录约定

- `re/`:一切逆向产物,已被 `.gitignore` 与提交守卫排除,不进仓库。
  - `re/ghidra/projects/dnf`:Ghidra 工程(导入并分析过的 `DNF.exe`)
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
~/Applications/ghidra_12.1.4_PUBLIC/support/analyzeHeadless re/ghidra/projects dnf \
  -process DNF.exe -noanalysis -readOnly -scriptPath tools/ghidra -postScript <脚本名>.java
```

注意:在 Shell 工具里要把长任务放进后台(`block_until_ms: 0`),否则命令结束时进程会被一起结束。

## 写进台账的格式

每条结论一行:ID、结论(观察 / 推断 / 未知)、来源(REA 带 Evidence ID,或非 REA 带可复现命令与脚本名)、置信度与未解释的部分。
函数只用地址与自拟名称,不用 Ghidra 的自动命名,也不贴反编译代码。

## 优先问题(与 P3 到 P5 并行推进)

1. 反汇编覆盖率:`nsn#0` 是不是真的能被完整读出来(函数数量、未解释字节占比)。
2. 谁引用了 VA `0x1A41FC8` 处的 `0x81A79011`:找到 PVF 读取与解密函数,与 `tools/py/dnfdata.py` 对照。
3. 标记流脚本的读取与分发:找到把 5 字节标记转成对象的函数,它的 switch 就是标签语义的原表。
4. Squirrel 的注册表:`sq_*` 名字不在明文里,先看有没有字符串解码函数(这一步如果需要解密就先问用户)。
5. `.ani` 的播放与判定:帧延时、图形特效、判定框的使用方式。
