# 证据台账

规则:

- **类型**:观察(直接读到的字节或直接跑出的结果)、推断(根据形状或取值猜的)、未知。
- **来源**:`REA` = REA 工具产出,带 Evidence ID;`非 REA` = 普通 Python 或 shell 测量,没有 REA 的证据等级,但给出可复现命令。
- 静态分析只说明磁盘上的字节是什么,不说明程序运行时做了什么。
- 本表不含任何从原版数据解出的素材、脚本或反编译代码。函数用地址描述,不用反编译器的自动命名。

目标二进制:`DNF.exe`,41,978,368 字节,SHA-256 `d422613fd86764f17398b742dfc2e9e6354c20ceded7d92876b49ded0d5ca0fe`。
用户自备数据所在目录由 `local.env` 的 `DNF_DIR` 指定。

## 数据层

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-001 | PVF 头、目录树、文件解密规则,以及全库 334,998 个文件的明文清单;聚合 SHA-256 为 `cb377e11…1154` | 观察 | 非 REA。`tools/py/dnfdata.py`、`tools/py/manifest.py` | `python3 tools/py/manifest.py --expect cb377e11645e024d9a3d775fdd613880bd520409382dbe961163878b65511154`(2026-10-14 在仓库副本上复跑,哈希一致,2.7 秒) | 高。目录项 `crc` 的性质(只是解密密钥)与目录树里多出的 1 字节**未解释** |
| E-002 | 1,575 个 NPK、80,464 个条目;80,328 个 IMG v2 的布局全部吻合;9,860,808 个非链接帧里 9,860,711 个解压长度恰好等于 `w*h*bpp`,另 97 个是 1×1 的截断占位帧;136 个旧版 IMG 未解析 | 观察 | 非 REA。`tools/py/scan_imgs.py` → `out/img_scan.json` | `python3 tools/py/scan_imgs.py`(约 6 秒);复跑结果与首次逐字段一致 | 高。像素颜色转换只以参考实现为准,未与原版画面逐像素比较 |
| E-003 | 标记流脚本语法(5 字节标记、类型 2 到 10)全库适用;3,408 种开标签、3,232,710 次出现;前 200 个覆盖 87.63% | 观察(语法)/ 未知(语义) | 非 REA。`tools/py/tag_catalog.py` → `out/tag_catalog.json` | `python3 tools/py/tag_catalog.py`(约 7 秒);复跑一致 | 语法高;语义没有标注 |
| E-004 | 二进制 `.ani` 结构:187,365 个全部恰好读到文件末尾;两个 `.ani` 指到 IMG 帧后拼出连贯动画 | 观察(结构)/ 推断(属性名与含义) | 非 REA。`tools/py/ani_spec.py` → `out/ani_verify.json` | `python3 tools/py/ani_spec.py`(约 16 秒);复跑一致 | 结构高;属性名是推断 |
| E-008 | **C# 数据层(`Dnf.Data`)与 Python 参考实现在全库上零差异**:PVF 334,998 个文件的路径、长度、明文 SHA-256 逐一一致,聚合哈希同为 `cb377e11…1154`(明文共 150,063,690 字节;stringtable 395,044 条);147,273 个标记流脚本、15,887,393 个标记、3,408 种开标签、3,232,710 次出现,以及 60 个扩展名下 4,184 行"标签 / 出现数 / 文件数 / 闭标签数"全部一致;187,365 个二进制 `.ani`(1,149,532 帧、181,149 个判定框)的 30 类属性与框计数、20 个最常见帧延时一致;1,575 个 NPK 的 80,328 个 IMG v2、11,754,348 个帧,每个 zlib 帧都校验 Adler-32,9,860,711 个帧解码长度恰好等于 `w*h*bpp`,另 97 个 1×1 截断占位帧,压缩 3,535,719,537 字节、解压 40,979,750,618 字节,计数逐项一致;随机 1000 帧(含稀有格式、链接帧、空帧与占位帧各 60 个以上)的 RGBA SHA-256 逐帧一致 | 观察(两个实现等价) | 非 REA。`src/Dnf.Cli`、`src/Dnf.Data.Tests`(43 个合成数据测试);报告 `out/p1_verification_all.json` | `tools/verify-p1.sh`。2026-10-14 在 Apple M4 Pro 上 18.7 秒(10 线程:PVF 2.0 秒、标记流 0.7 秒、ANI 0.7 秒、IMG 15.3 秒、像素 < 0.1 秒)。另做了变异检验:改动参考输出里的 1 个像素哈希、1 个标签计数、1 个 ANI 统计、1 个 IMG 计数、清单里 1 行哈希,五个检查全部报失败 | 等价性:高。**正确性:有限**——两个实现按我从同一批数据归纳出的同一份规格写成,所以这条只证明 C# 忠实实现了规格,不证明规格与原版引擎一致。规格的独立旁证只有:所有帧的解码长度与 `w*h*bpp` 恰好相等,以及此前对渲染结果的目视检查;与原版画面的逐像素对比要等 P0 的基线录制。136 个旧版 IMG 与 49 个重名 IMG 的覆盖顺序仍未覆盖 |

## DNF.exe

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-005 | PE32 x86,时间戳 2021-03-19 16:02:58 UTC,ImageBase 0x400000,入口 RVA 0xFE06BA 落在第 0 个节。11 个节:9 个同名 `nsn`(#7 为 `XR`,其余 `XRW`)、`.idata`、`.rsrc`。导入目录 RVA 0x2898000(大小 0x280),TLS 目录 RVA 0x14C47C4,TLS 回调数组在 VA 0x156202C(Ghidra 导入日志) | 观察(静态字节) | 非 REA。`tools/py/pe_probe.py` → `out/pe_probe.json` | `python3 tools/py/pe_probe.py` | 高 |
| E-005a | 节熵:`nsn#0` 6.66(0% 高熵块)、`#1` 6.30、`#2` 2.35、`#3` 7.65(48.1% 高熵块)、`#4` 5.85、`#5` 6.42、`#6` 7.29(0%)、`#7` 7.94(79.9%)、`#8` 6.52;`.rsrc` 7.82(90.7%)。高熵块定义:4 KiB 块的香农熵 > 7.5。全部节合计 17.0% | 观察 | 同 E-005 | 同 E-005 | 高 |
| E-005b | **推断**:`nsn#0`(约 17.3 MiB,含入口点)的熵与字节分布接近未加密的 x86 代码,值得做静态反汇编;`#3`、`#7` 与 `.rsrc` 的高熵块是密文、压缩数据或不可压缩资源,**内容未知** | 推断 | 由 E-005a 推出 | Ghidra 导入后看反汇编覆盖率与函数数量来检验 | 中,待检验 |
| E-005c | 明文可见:`Neople Img File`、`Neople Image File`、`NeoplePack_Bill`(各 1 处,`nsn#1`);zlib 版权串 2 处(`nsn#1`);`TerSafe` 1 处(`nsn#5`);UTF-16 的 `NeopleEngine.LOG` 1 处(`nsn#2`)。**ASCII 与 UTF-16 下都找不到**:`Script.pvf`、`ImagePacks2`、`stringtable.bin`、`puchikon@neople`、`sq_`、Squirrel 版权串 | 观察 | 同 E-005 | 同 E-005 | 高。"找不到"只说明磁盘字节里没有这些明文,不说明它们不存在 |
| E-005d | 常量 `0x81A79011`(PVF 解密常量)在整个文件里只出现 1 次,位于 `nsn#2` 的数据区(VA 0x1A41FC8),前面是 8 个零字节,后面紧跟一个指向映像内的 32 位值 `0x018AA350`。**不是**代码里的立即数。`ror r32,6` 的字节模式共 220 处,195 处在 `nsn#0` | 观察 | 同 E-005 | 同 E-005 | 高(位置);用途**未知**,需要看引用它的代码 |
| E-006 | `.rsrc` 共 18 个资源,语言 ID 全部为 1042:6 个自定义类型 `FILE`(ID 115、157、159、160、161、163;长度 483,060、406,756、483,112、436,532、549,400、509,508 字节)、6 个自定义类型 `HSH`(ID 125、126、131、132、133、134;长度 6,548、5,976、21,847、21,792、892、1,192 字节)、4 个 RT_ICON、1 个 RT_GROUP_ICON、1 个 RT_VERSION。除图标组外负载均不透明,每个负载的 SHA-256 在 REA 结果里 | 观察(REA) | **REA** `inspect_pe_resources`,Evidence ID `ev_49954970e46f52bd91fed6520aed836b5f11db9ed97b322108f007ce0baf63ed`,覆盖状态 complete(检查 41 个目录项) | 用同一文件路径重新调用 `inspect_pe_resources` | 高。`FILE` 与 `HSH` 的用途**未知**,它们属于保护层候选,**不解码、不解密** |

## Ghidra 静态分析(状态:导入与自动分析进行中)

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-007 | 用 Ghidra 12.1.4 headless 导入 `DNF.exe`:加载器 PE,语言 `x86:LE:32:default:windows`;工程在 `re/ghidra/projects/dnf`(已被 .gitignore 排除) | 观察 | 非 REA。`re/ghidra/logs/import_analyze.log` | 见 [逆向工作流](../reverse-workflow.md) | 待分析完成后补结果 |

## 继承自前期 DNF-CX 研究的 REA 证据(启动器层,不是 DNF.exe)

这些是前一轮研究产出的 Evidence ID,对象是 DNF-CX 应用包、启动器和 `ijl15.dll`,保留作来源记录。

| 内容 | Evidence ID |
|---|---|
| 应用包清单(只有一个 Mach-O) | `ev_a0cc099df23c6cf9f4f565aaf9d733959d2de2ef48a334a10ad8ac3c61f19de7` |
| 应用包清单的父级证据 | `ev_ca9c8c23b564bfbd2b2c743aede638cb602deda028fbf52e8402dcee2e1f6b47` |
| DNFBossKey 的 Mach-O 结构 | `ev_3872442c1d5ecf7bd21459f677da10382afc6faa982b03b390c62947497f6cfd` |
| DNFBossKey 的签名 | `ev_3ef0621aef915065320321d05850504a1db3468b451ad5e335ae8ed54285007e` |
| dnf-launcher.exe 的 PE 资源与版本信息 | `ev_4757ffdd25c786d7427c593efd486afb9afda05ea2cbf9f81e55b0da86f9ec7a` |
| ijl15.dll 的 PE 资源 | `ev_2606714cf6d019759532c5e2e30d7c296c34558c7f76563dbe1bc03e7f332a88` |
| ijl15.dll 概览 | `ev_906b7e07449013f0c43e3798739ab593890e655d55cf049f07423783a59f0cef` |
| ijl15.dll 字符串 | `ev_cb3195f3886124706b5d3ad5517db1f1063a7ad265bb4a236439abd2fa5de8d9` |
| ijl15.dll 过程列表 | `ev_c60cd20e24a3c5ef6265eb9392c140ef5267159163eaa3eab4c3c7780e6c0b58` |
| `DllMain` 函数档案 | `ev_13fd1c26c7d1f639d37b856d5f4ec89eea84e4bbd79a66525e71560640c61571` |
| `DllMain` 被调函数视图 | `ev_e91557702d88e2b260999b6ebb57d1badb9dee498e2226d3bda48092cb389b5c` |
| `DllMain` 伪代码视图 | `ev_f32c784ad8464f2fce7500b6ab0520eface49b8579443d121ef90fd5afcdb853` |

## 覆盖限制

- REA 的 `binary_overview` 与 `list_segments` 对 17.7 MiB 的启动器在 MCP 超时内没有返回(底层是 Ghidra 全量分析)。`DNF.exe` 约 40 MiB,同样的限制很可能适用,所以主分析走 Ghidra headless,REA 只用在不需要完整分析的目标无关工具上(如 PE 资源)。
- 上面所有非 REA 条目都是只读测量,没有运行游戏,没有修改任何游戏文件。
