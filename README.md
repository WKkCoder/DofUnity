# dnf-unity

完整逆向《地下城与勇士》(DNF)客户端,并用 Unity 6 LTS 重新实现,目标平台 Windows、macOS、Android、iOS。
运行时读取**用户自备**的原版数据(`Script.pvf`、`ImagePacks2/`),本仓库不包含、不分发任何原版内容。

## 现状

| 阶段 | 状态 |
|---|---|
| P1 数据层 | Python 参考实现已在全库验证(PVF 334,998 个文件;80,328 个 IMG v2;187,365 个 `.ani` 全部吃完)。C# 库未开始 |
| P2 语义目录 | 脚本语法已破解,tag 目录已生成(3,408 种);语义待标注 |
| P6 逆向 | `DNF.exe` 已做只读探查,Ghidra 导入与分析进行中 |

详见 [路线图](docs/roadmap.md)、[格式规格](docs/formats.md)、[证据台账](docs/evidence/ledger.md)、[范围与边界](docs/boundaries.md)、[逆向工作流](docs/reverse-workflow.md)。

## 目录

```
docs/        路线图、格式规格、边界、证据台账
tools/py/    Python 参考实现与扫描脚本(只读,标准库)——C# 库的对照标准
tools/guard/ 提交前守卫:挡住原版内容与反编译输出
src/         C# 数据层库与命令行(待建)
unity/       Unity 工程(待建)
re/  out/    逆向产物与生成物,已被 .gitignore 排除
```

## 使用

1. 复制 `local.env.example` 为 `local.env`,把 `DNF_DIR` 改成你自己的游戏目录(里面应有 `Script.pvf` 与 `ImagePacks2/`)。
2. 安装提交守卫:`sh tools/guard/install-hooks.sh`
3. 跑参考实现(只读):

```sh
python3 tools/py/manifest.py    # PVF 文件清单与聚合哈希,期望 cb377e11…1154
python3 tools/py/scan_imgs.py   # 全量扫描 IMG v2
python3 tools/py/tag_catalog.py # tag 目录与覆盖率
python3 tools/py/ani_spec.py    # 二进制 .ani 全库验证
python3 tools/py/pe_probe.py    # DNF.exe 静态探查
```

## 测试策略

- **合成数据测试**(可提交):测试里自己构造最小的 PVF、NPK、IMG,验证解析器。
- **全库对拍**(只在本机跑,数据不提交):C# 实现与 Python 参考实现、PVF 文件清单逐项比较;随机抽 1000 帧逐像素比较。
- **差分测试**(P3 以后):同样输入在原版客户端(Wine)与 Unity 里各跑一遍再比较。
