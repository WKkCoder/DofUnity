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

已完成:

1. ~~反汇编覆盖率~~:`nsn#0` 87.69% 被解成指令、48,013 个函数(E-010)。
2. ~~谁引用了 `0x81A79011`~~:只有 VA `0x011AB180` 一个函数,它同时做解密与 CRC-32 校验,规则在全库上验证(E-011)。

待做:

3. 标记流脚本的读取与分发:找到把 5 字节标记转成对象的函数,它的 switch 就是标签语义的原表。入口线索:PVF 文件读取的调用者(VA `0x011ACA20` 与 `0x011ACB40` 的上游,即各类资源的加载函数,如 `0x01175620`、`0x01178150`、`0x01179CC0`)。
4. Squirrel 的注册表:`sq_*` 名字不在明文里,先看有没有字符串解码函数(这一步如果需要解密就先问用户)。
5. `.ani` 的播放与判定:帧延时、图形特效、判定框的使用方式。
