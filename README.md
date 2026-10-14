# dnf-unity

完整逆向《地下城与勇士》(DNF)客户端,并用 Unity 6 LTS 重新实现,目标平台 Windows、macOS、Android、iOS。
运行时读取**用户自备**的原版数据(`Script.pvf`、`ImagePacks2/`),本仓库不包含、不分发任何原版内容。

## 现状

| 阶段 | 状态 |
|---|---|
| P1 数据层 | **C# 库与命令行已完成,全库对拍零差异**(PVF 334,998 个文件哈希逐一一致;147,273 个标记流脚本;187,365 个 `.ani`;80,328 个 IMG、9,860,711 个帧解码并校验 Adler-32;随机 1000 帧逐像素一致)。Unity 编辑器里编译该包待验证 |
| P2 语义目录 | 脚本语法已破解,tag 目录已生成(3,408 种);语义待标注 |
| P6 逆向 | `DNF.exe` 已做只读探查,Ghidra 导入与分析进行中 |

详见 [路线图](docs/roadmap.md)、[格式规格](docs/formats.md)、[证据台账](docs/evidence/ledger.md)、[范围与边界](docs/boundaries.md)、[逆向工作流](docs/reverse-workflow.md)。

## 目录

```
docs/        路线图、格式规格、边界、证据台账
tools/py/    Python 参考实现与扫描脚本(只读,标准库)——C# 库的对照标准
tools/guard/ 提交前守卫:挡住原版内容与反编译输出
packages/    com.dnfunity.data:Unity 包(数据层库的唯一源码)
src/         Dnf.Data(netstandard2.1 工程,链接上面的源码)、Dnf.Data.Tests(合成数据单元测试)、Dnf.Cli(全库对拍命令行)
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

4. 一条命令验收 P1(单元测试 + C# 全库对拍,约 20 秒;参考输出缺失时会先用上面的脚本生成):

```sh
tools/verify-p1.sh
```

需要用户级 .NET SDK(`tools/env.sh` 会把 `~/.dotnet` 加进当前 shell 的 PATH)。报告写到 `out/p1_verification_all.json`。

## 测试策略

- **合成数据测试**(可提交):测试里自己构造最小的 PVF、NPK、IMG,验证解析器。
- **全库对拍**(只在本机跑,数据不提交):C# 实现与 Python 参考实现、PVF 文件清单逐项比较;随机抽 1000 帧逐像素比较。
- **差分测试**(P3 以后):同样输入在原版客户端(Wine)与 Unity 里各跑一遍再比较。
