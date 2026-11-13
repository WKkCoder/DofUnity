# 文档索引

仓库概览、当前进度与快速开始在根目录的 [README](../README.md);这里是 `docs/` 下全部文档的导航。
文档用中文写,结论区分**观察 / 推断 / 未知**,规则的来源写成证据编号(`E-xxx`),编号的详情在[证据台账](evidence/ledger.md)。

## 先看这几份

| 文档 | 内容 |
|---|---|
| [roadmap.md](roadmap.md) | 路线图:P0–P8 各阶段做什么、怎么验收、依赖谁、现状如何 |
| [flow.md](flow.md) | 游戏流程骨架与最新进展,按章节记(§1–§114,每章对应一次里程碑)。**联网(P7)从 §75 起** |
| [boundaries.md](boundaries.md) | 范围与边界:硬边界、证据纪律、版权 |
| [usage.md](usage.md) | 使用手册:环境、命令行、构建与播放、各类检查命令、测试策略、看门狗(定期清理 agent 残留的孤儿进程与垃圾资源)、发布到 GitHub(脱敏脚本) |
| [progress-log.md](progress-log.md) | 整理前 README 里按阶段写的里程碑流水账(历史存档,不再更新) |

## 数据与格式

| 文档 | 内容 |
|---|---|
| [formats.md](formats.md) | DNF 数据格式规格:PVF、`stringtable.bin`、标记流脚本、二进制 `.ani`、NPK 与 IMG v2 |
| [act-script.md](act-script.md) | `.act` 动作脚本:语法与运行时语义 |
| [ai-script.md](ai-script.md) | `.ai` 决策树脚本 |
| [engine-objects.md](engine-objects.md) | 引擎里的对象:类层次与种类码 |

四份表格(TSV,可用表格软件或 `column -t -s$'\t'` 查看):

| 文件 | 内容 |
|---|---|
| [act-tags.tsv](act-tags.tsv) | `.act` 全部标签:类型号、归属、全库出现次数与证据 |
| [act-semantics.tsv](act-semantics.tsv) | `.act` 运行器各标签的语义摘要、置信度与未决项 |
| [sq-api-catalog.tsv](sq-api-catalog.tsv) | 脚本调用的原生接口静态清单:调用次数、涉及文件数、参数个数分布、常见接收者 |
| [native-audit.tsv](native-audit.tsv) | 原生接口逐个的实现状态与核对备注 |

## 逆向与证据

| 文档 | 内容 |
|---|---|
| [evidence/ledger.md](evidence/ledger.md) | **证据台账**(E-001 至 E-407,已分卷):活跃卷为本文件(E-300 起);冻结归档为 [ledger-archive-1.md](evidence/ledger-archive-1.md)(E-001 至 E-106)与 [ledger-archive-2.md](evidence/ledger-archive-2.md)(E-107 至 E-299)。按编号搜,不要通读 |
| [reverse-workflow.md](reverse-workflow.md) | 逆向工作流:对 `DNF.exe` 做静态分析的做法 |
| [baseline.md](baseline.md) | 对照基线:原版客户端怎么跑、怎么截、截哪些场景(S01–S10) |
| [hit-pipeline.md](hit-pipeline.md) | 命中与受伤流水线(客户端 `DNF.exe` 的静态读取结果) |
| [coverage.md](coverage.md) | 覆盖率与可追溯性:证据被代码与文档引用的情况(由 `tools/py/coverage.py` 生成) |

## 游戏系统(单机)

| 文档 | 内容 |
|---|---|
| [town.md](town.md) | 可走的城镇与加载画面 |
| [char-select.md](char-select.md) | 选角界面与登录占位 |
| [dungeon-select.md](dungeon-select.md) | 副本选择界面:世界地图 |
| [settlement.md](settlement.md) | 副本结算:压暗的场景、营火、德利拉与「是否继续?」菜单 |
| [monster-ability.md](monster-ability.md) | 怪物数值的计算(服务端 `set_monster_ability` 的移植) |
| [monster-ai.md](monster-ai.md) | 怪物出招决策与默认 AI |
| [monster-projectile.md](monster-projectile.md) | 怪物投射物 |
| [monster-hp.md](monster-hp.md) | 目标怪物血条窗口 |
| [damage-input.md](damage-input.md) | 主角打怪物的伤害输入 |
| [damage-number.md](damage-number.md) | 伤害数字 |
| [combo-counter.md](combo-counter.md) | 连击计数 |
| [dungeon-minimap.md](dungeon-minimap.md) | 右上角副本小地图 |
| [hud.md](hud.md) | 界面底栏、魔法值、经验条与技能槽 |
| [exp.md](exp.md) | 经验与升级 |
| [audio.md](audio.md) | 声音:音效、背景音乐、界面与场景音乐 |
| [mobile.md](mobile.md) | 移动端:触屏操作层与数据目录 |

## 约定

- **证据编号**:`E-xxx`,单调递增;台账里每条记录结论、类型(观察 / 推断 / 未知)、来源、复现方法与置信度。引用时写编号,不复制结论。
- **REA / 非 REA**:用逆向分析工具得到的结论才算 REA 证据;普通 shell 或 Python 的测量不是,台账里单独标注。
- **没有原版内容**:文档里只写结构、数字、规则与证据编号,不贴原版脚本、素材或反编译代码(提交守卫会拦)。
