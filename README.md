# dnf-unity

完整逆向《地下城与勇士》(DNF)客户端,并用 Unity 6 LTS 重新实现,目标平台 Windows、macOS、Android、iOS。
运行时读取**用户自备**的原版数据(`Script.pvf`、`ImagePacks2/`),本仓库不包含、不分发任何原版内容。

## 现状

| 阶段 | 状态 |
|---|---|
| P1 数据层 | **C# 库与命令行已完成,全库对拍零差异**(PVF 334,998 个文件哈希逐一一致;147,273 个标记流脚本;187,365 个 `.ani`;80,328 个 IMG、9,860,711 个帧解码并校验 Adler-32;随机 1000 帧逐像素一致)。Unity 6 里编译并运行该包已验证(9 个 EditMode 测试通过,含读取真实 PVF 全部文件并校验目录树与每个文件的 CRC-32)。**像素规则已用原版客户端的屏幕输出校验**:在一张城镇截图里识别出 33 个精灵,解码结果与屏幕逐像素一致;校验同时更正了 1555 的 5 位通道展开(`round(v*255/31)`,不是位复制),见证据 E-023 |
| P2 语义目录 | 脚本语法已破解,tag 目录已生成(3,408 种);语义待标注 |
| P3 动画与渲染 | 进行中:位置模型(E-024)与像素规则(E-023)已对上原版的一个城镇场景;纯 C# 合成器 `Dnf.Render` 与 `dnf render-town` 复现了同样的数字(E-027);**Unity 的 GPU 渲染路径(`Dnf.Client`)与 CPU 合成器逐字节一致**(Metal,编辑器与 macOS 播放器,含真实城镇 NPC,E-028);**NPC 朝向与帧缩放导致的水平镜像已被原版截图证实**(露娜 2,519 / 2,519 个不透明像素逐像素一致,E-029);其余三个平台的 GPU 未验证;下一步是动画播放与更多场景元素 |
| P6 逆向 | Ghidra 对 `DNF.exe` 的只读静态分析已完成;已定位 PVF 解密与校验例程,并发现每个文件的 `crc` 同时是 CRC-32 校验值(全库 334,998 个文件与目录树通过,E-011) |

详见 [路线图](docs/roadmap.md)、[格式规格](docs/formats.md)、[证据台账](docs/evidence/ledger.md)、[范围与边界](docs/boundaries.md)、[逆向工作流](docs/reverse-workflow.md)。

## 目录

```
docs/        路线图、格式规格、边界、证据台账
tools/py/    Python 参考实现与扫描脚本(只读,标准库)——C# 库的对照标准
tools/guard/ 提交前守卫:挡住原版内容与反编译输出
packages/    com.dnfunity.data:Unity 包(数据层库的唯一源码);com.dnfunity.render:纯 C# 的 CPU 合成器与城镇场景(Unity GPU 路径的参照实现);com.dnfunity.client:Unity 层(800×600 像素对齐的舞台、GPU 绘制、帧读回)
src/         Dnf.Data、Dnf.Render(netstandard2.1 工程,链接上面的源码)、Dnf.Data.Tests、Dnf.Render.Tests(合成数据单元测试)、Dnf.Cli(全库对拍与画面对照命令行)
unity/       Unity 工程 DnfUnity(引用 packages/com.dnfunity.data;目前只有 EditMode 测试)
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
python3 tools/py/pvf_crc_probe.py # PVF 每个文件与目录树的 CRC-32 校验(客户端自己的规则,E-011)
```

4. 一条命令验收 P1(单元测试 + C# 全库对拍 + 客户端校验规则,约 30 秒;参考输出缺失时会先用上面的脚本生成):

```sh
tools/verify-p1.sh
```

需要用户级 .NET SDK(`tools/env.sh` 会把 `~/.dotnet` 加进当前 shell 的 PATH)。报告写到 `out/p1_verification_all.json`。

5. 在 Unity 里跑测试(批处理模式,不需要登录):`tools/unity-test.sh`。结果在 `out/unity-EditMode-results.xml`。
   加 `UNITY_GRAPHICS=1` 会去掉 `-nographics`,让 GPU 与 CPU 合成器的对照真正运行(需要 GPU);`UNITY_GRAPHICS=1 tools/unity-build-smoke.sh macos` 在 macOS 播放器里再做一遍。

6. 对照原版画面(需要一张原版客户端的 800×600 截图,窗口内容 1:1,放在 `re/` 下;截图不入库):

```sh
# 在全部 IMG 帧里找"不透明像素与截图完全相同"的帧,并给出它们在截图里的位置(约 20 秒)
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll locate --shot <截图.png> --min-ratio 0.99 --json out/locate.json
# 用 Python 参考实现独立复核,同时比较 1555 的两种展开规则
python3 tools/py/screen_match_check.py --shot <截图.png> --locate out/locate.json
```

7. 用我们自己的合成器画城镇里的 NPC,并与同一张原版截图逐像素对照(约 1 秒;`--png` 输出 左=我们、中=原版、右=对照 的三联图):

```sh
dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll render-town --shot <截图.png> --best-frame --png re/notes/town/render_town.png
```

## 测试策略

- **合成数据测试**(可提交):测试里自己构造最小的 PVF、NPK、IMG,验证解析器。
- **全库对拍**(只在本机跑,数据不提交):C# 实现与 Python 参考实现、PVF 文件清单逐项比较;随机抽 1000 帧逐像素比较。
- **差分测试**(P3 以后):同样输入在原版客户端(Wine)与 Unity 里各跑一遍再比较。
