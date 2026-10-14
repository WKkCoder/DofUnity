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
| E-001 | PVF 头、目录树、文件解密规则,以及全库 334,998 个文件的明文清单;聚合 SHA-256 为 `cb377e11…1154` | 观察 | 非 REA。`tools/py/dnfdata.py`、`tools/py/manifest.py` | `python3 tools/py/manifest.py --expect cb377e11645e024d9a3d775fdd613880bd520409382dbe961163878b65511154`(2026-10-14 在仓库副本上复跑,哈希一致,2.7 秒) | 高。此前"`crc` 只是解密密钥"与"目录树里多出的 1 字节未解释"这两处**已在 E-011 更正并解释**(`crc` 同时是 CRC-32 校验值;多出的字节是零填充) |
| E-002 | 1,575 个 NPK、80,464 个条目;80,328 个 IMG v2 的布局全部吻合;9,860,808 个非链接帧里 9,860,711 个解压长度恰好等于 `w*h*bpp`,另 97 个是 1×1 的截断占位帧;136 个旧版 IMG 未解析 | 观察 | 非 REA。`tools/py/scan_imgs.py` → `out/img_scan.json` | `python3 tools/py/scan_imgs.py`(约 6 秒);复跑结果与首次逐字段一致 | 高(布局与长度)。像素颜色转换已用原版屏幕输出校验过一个场景,见 E-023;这次校验更正了 1555 的 5 位展开规则 |
| E-003 | 标记流脚本语法(5 字节标记、类型 2 到 10)全库适用;3,408 种开标签、3,232,710 次出现;前 200 个覆盖 87.63% | 观察(语法)/ 未知(语义) | 非 REA。`tools/py/tag_catalog.py` → `out/tag_catalog.json` | `python3 tools/py/tag_catalog.py`(约 7 秒);复跑一致 | 语法高;语义没有标注 |
| E-004 | 二进制 `.ani` 结构:187,365 个全部恰好读到文件末尾;两个 `.ani` 指到 IMG 帧后拼出连贯动画 | 观察(结构)/ 推断(属性名与含义) | 非 REA。`tools/py/ani_spec.py` → `out/ani_verify.json` | `python3 tools/py/ani_spec.py`(约 16 秒);复跑一致 | 结构高;属性名是推断 |
| E-008 | **C# 数据层(`Dnf.Data`)与 Python 参考实现在全库上零差异**:PVF 334,998 个文件的路径、长度、明文 SHA-256 逐一一致,聚合哈希同为 `cb377e11…1154`(明文共 150,063,690 字节;stringtable 395,044 条);147,273 个标记流脚本、15,887,393 个标记、3,408 种开标签、3,232,710 次出现,以及 60 个扩展名下 4,184 行"标签 / 出现数 / 文件数 / 闭标签数"全部一致;187,365 个二进制 `.ani`(1,149,532 帧、181,149 个判定框)的 30 类属性与框计数、20 个最常见帧延时一致;1,575 个 NPK 的 80,328 个 IMG v2、11,754,348 个帧,每个 zlib 帧都校验 Adler-32,9,860,711 个帧解码长度恰好等于 `w*h*bpp`,另 97 个 1×1 截断占位帧,压缩 3,535,719,537 字节、解压 40,979,750,618 字节,计数逐项一致;随机 1000 帧(含稀有格式、链接帧、空帧与占位帧各 60 个以上)的 RGBA SHA-256 逐帧一致 | 观察(两个实现等价) | 非 REA。`src/Dnf.Cli`、`src/Dnf.Data.Tests`(当时 43 个合成数据测试,加入 E-011 的校验后为 56 个,加入 E-023 的像素向量后为 58 个);报告 `out/p1_verification_all.json` | `tools/verify-p1.sh`。2026-10-14 在 Apple M4 Pro 上 18.7 秒(10 线程:PVF 2.0 秒、标记流 0.7 秒、ANI 0.7 秒、IMG 15.3 秒、像素 < 0.1 秒)。另做了变异检验:改动参考输出里的 1 个像素哈希、1 个标签计数、1 个 ANI 统计、1 个 IMG 计数、清单里 1 行哈希,五个检查全部报失败 | 等价性:高。**正确性:有限**——两个实现按我从同一批数据归纳出的同一份规格写成,所以这条只证明 C# 忠实实现了规格,不证明规格与原版引擎一致。规格的独立旁证只有:所有帧的解码长度与 `w*h*bpp` 恰好相等,以及此前对渲染结果的目视检查;与原版画面的逐像素对比现在有一个场景(E-023),它发现并更正了 1555 的 5 位通道展开规则(位复制 → `round(v*255/31)`);更正后参考实现与 C# 一起改、像素基准 `out/oracle_pixels.json` 重新生成、`tools/verify-p1.sh` 全部通过,所以上面"随机 1000 帧一致"现在指的是新规则。136 个旧版 IMG 与 49 个重名 IMG 的覆盖顺序仍未覆盖 |
| E-009 | **Unity 6000.3.26f1 能编译并运行数据层包**:Unity 批处理模式(未登录)新建工程 `unity/DnfUnity` 成功;本地 UPM 包 `com.dnfunity.data` 编译无警告;6 个 EditMode 测试全部通过(加入 E-011 的校验后为 9 个),其中一个用 Unity 的 Mono 运行时读出 `Script.pvf` 全部 334,998 个文件并核对聚合哈希(约 4 秒);另有 Python 参考向量下的解密、像素转换与 zlib 帧解码 | 观察 | 非 REA。`unity/DnfUnity/Assets/Tests/EditMode`;结果 `out/unity-EditMode-results.xml` | `tools/unity-test.sh`(约 8 秒)。日志里有 `Access token is unavailable` 的授权告警,但不影响批处理新建工程、编译与测试 | 中高。编辑器里是 Mono,**不等于** IL2CPP/AOT 的移动端运行时;未登录状态下的播放器构建见 E-021 |
| E-021 | **Unity 播放器能在不登录的情况下构建,macOS 播放器里数据层冒烟 10/10 通过**:macOS(Mono,未签名)播放器 106.6 MB,构建后无头运行,CRC-32 三组种子、PVF 解密向量、ARGB4444/ARGB1555(按 E-023 的取整规则)/ARGB8888 像素转换、带 zlib 帧的合成 IMG v2(含 Adler-32)、SHA-256 共 10 项检查全部通过(`backend=mono platform=OSXPlayer unity=6000.3.26f1`);Windows x64(Mono)播放器可以在 macOS 上构建(**没有运行**);Android IL2CPP ARM64 APK 111 MB(首次约 135 秒,**没有安装运行**)。iOS **未验证**:没装 Unity iOS 模块,也没有完整 Xcode | 观察 | 非 REA。`unity/DnfUnity/Assets/Smoke`(运行时冒烟)、`Assets/Editor/BuildSmoke.cs`(构建入口)、`tools/unity-build-smoke.sh`;日志在 `re/`(不入库) | `./tools/unity-build-smoke.sh macos`(约 10 秒,含无头运行并检查 `DNF_SMOKE_RESULT`);`windows`、`android` 同理 | 中高:证明数据层在 Unity 的 Mono 播放器里行为正确、三个平台的播放器都能打包;IL2CPP/AOT 与真机下的运行行为要等有设备的阶段验证 |

## DNF.exe

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-005 | PE32 x86,时间戳 2021-03-19 16:02:58 UTC,ImageBase 0x400000,入口 RVA 0xFE06BA 落在第 0 个节。11 个节:9 个同名 `nsn`(#7 为 `XR`,其余 `XRW`)、`.idata`、`.rsrc`。导入目录 RVA 0x2898000(大小 0x280),TLS 目录 RVA 0x14C47C4,TLS 回调数组在 VA 0x156202C(Ghidra 导入日志) | 观察(静态字节) | 非 REA。`tools/py/pe_probe.py` → `out/pe_probe.json` | `python3 tools/py/pe_probe.py` | 高 |
| E-005a | 节熵:`nsn#0` 6.66(0% 高熵块)、`#1` 6.30、`#2` 2.35、`#3` 7.65(48.1% 高熵块)、`#4` 5.85、`#5` 6.42、`#6` 7.29(0%)、`#7` 7.94(79.9%)、`#8` 6.52;`.rsrc` 7.82(90.7%)。高熵块定义:4 KiB 块的香农熵 > 7.5。全部节合计 17.0% | 观察 | 同 E-005 | 同 E-005 | 高 |
| E-005b | **推断**:`nsn#0`(约 17.3 MiB,含入口点)的熵与字节分布接近未加密的 x86 代码,值得做静态反汇编;`#3`、`#7` 与 `.rsrc` 的高熵块是密文、压缩数据或不可压缩资源,**内容未知** | 推断 | 由 E-005a 推出 | Ghidra 导入后看反汇编覆盖率与函数数量来检验 | **已检验:`nsn#0` 部分成立(E-010:87.69% 被解成指令);`#7` 不是可反汇编的代码;`#3` 一半是代码一半是高熵数据**。其余(密文、压缩还是资源)仍未知 |
| E-005c | 明文可见:`Neople Img File`、`Neople Image File`、`NeoplePack_Bill`(各 1 处,`nsn#1`);zlib 版权串 2 处(`nsn#1`);`TerSafe` 1 处(`nsn#5`);UTF-16 的 `NeopleEngine.LOG` 1 处(`nsn#2`)。**ASCII 与 UTF-16 下都找不到**:`Script.pvf`、`ImagePacks2`、`stringtable.bin`、`puchikon@neople`、`sq_`、Squirrel 版权串 | 观察 | 同 E-005 | 同 E-005 | 高。"找不到"只说明磁盘字节里没有这些明文,不说明它们不存在 |
| E-005d | 常量 `0x81A79011`(PVF 解密常量)在整个文件里只出现 1 次,位于 `nsn#2` 的数据区(VA 0x1A41FC8),前面是 8 个零字节,后面紧跟一个指向映像内的 32 位值 `0x018AA350`。**不是**代码里的立即数。`ror r32,6` 的字节模式共 220 处,195 处在 `nsn#0` | 观察 | 同 E-005 | 同 E-005 | 高(位置);用途已在 E-011 查明:它是一个全局变量,只被 VA 0x011AB180 的解密校验例程读取一次 |
| E-006 | `.rsrc` 共 18 个资源,语言 ID 全部为 1042:6 个自定义类型 `FILE`(ID 115、157、159、160、161、163;长度 483,060、406,756、483,112、436,532、549,400、509,508 字节)、6 个自定义类型 `HSH`(ID 125、126、131、132、133、134;长度 6,548、5,976、21,847、21,792、892、1,192 字节)、4 个 RT_ICON、1 个 RT_GROUP_ICON、1 个 RT_VERSION。除图标组外负载均不透明,每个负载的 SHA-256 在 REA 结果里 | 观察(REA) | **REA** `inspect_pe_resources`,Evidence ID `ev_49954970e46f52bd91fed6520aed836b5f11db9ed97b322108f007ce0baf63ed`,覆盖状态 complete(检查 41 个目录项) | 用同一文件路径重新调用 `inspect_pe_resources` | 高。`FILE` 与 `HSH` 的用途**未知**,它们属于保护层候选,**不解码、不解密** |

## Ghidra 静态分析(状态:导入与自动分析已完成;只读静态,未运行 DNF.exe)

以下条目的来源都是 Ghidra 12.1.4 headless(**非 REA**,没有 REA Evidence ID)。脚本在 `tools/ghidra/`,工程与日志在 `re/ghidra/`(已被 .gitignore 与提交守卫排除)。函数一律用虚拟地址标识,不引用反编译器的自动命名,也不提交任何反汇编或反编译文本。

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-007 | 用 Ghidra 12.1.4 headless 导入 `DNF.exe` 并完成自动分析:加载器 PE,语言 `x86:LE:32:default`(编译器规范 windows),镜像基址 0x400000;自动分析耗时 2,178 秒,日志 `Analysis succeeded`、`Save succeeded`。工程在 `re/ghidra/projects/dnf`(484 MB) | 观察 | 非 REA(Ghidra)。`re/ghidra/logs/import_analyze.log` | 见 [逆向工作流](../reverse-workflow.md) | 高(工具自己报告成功;分析结果的正确性按下面各条分别评估) |
| E-010 | **程序总览与各节的代码覆盖**:映射 63,028,736 字节,解成 5,809,782 条指令(18,407,896 字节)、101,840 个函数。`nsn#0`(VA 0x401000–0x1559FFF,18.19 MB):87.69% 的字节被解成指令,48,013 个函数。`nsn#3`(0x1DD9000–0x20EEFFF,3.2 MB):50.47%、41,934 个函数(平均约 39 字节,都很短)。`nsn#5`(0x20FF000–0x2169FFF):81.45%、7,226 个函数。`nsn#7`(0x297C000–0x2C91FFF,3.2 MB,熵 7.94):只有 0.08%(2,616 字节)被解成指令,**不是可直接反汇编的代码**。`nsn#6`(0x216A000,8.4 MB):4.48%,基本是非代码数据。`nsn#8`(0x2C92000–0x2C97FFF,24 KB):68.26%、529 个函数。`.idata` 17.2 MB 与 `.rsrc` 3.2 MB 基本不含代码。`ror r32,6` 共 356 条(163 个函数里,2 条不在任何函数内):193 条在 `nsn#0`、136 条在 `nsn#3`、19 条在 `nsn#5`、7 条在 `nsn#6`;这些里只有 1 个函数读取了 `0x81A79011`(E-011),其余与 PVF 解密无关 | 观察 | 非 REA(Ghidra)。`tools/ghidra/P6Survey.java` → `re/ghidra/out/blocks.tsv`、`ror6.tsv`、`summary.txt`(不入库) | `analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly -scriptPath tools/ghidra -postScript P6Survey.java re/ghidra/out`(约 15 秒) | 覆盖率是反汇编器的统计,不等于"这些字节确实是被程序执行的代码";`nsn#3` 里大量短函数的用途**未知**。早先的"`ror r32,6` 字节模式 220 处"是粗略计数,以 Ghidra 的 356 条指令为准 |
| E-011 | **PVF 解密与校验例程已定位,并在全库上验证**。VA 0x011AB180(177 字节)对一段缓冲区逐 32 位字做:`(可选)异或全局变量 A`(VA 0x01A41FC8,磁盘镜像里就是 `0x81A79011`)→ 异或密钥(密钥先与全局变量 B 异或,B 在 VA 0x01B08BC0、磁盘镜像里为 0,非零才生效)→ 循环右移 6 位 → 存回;同时对**存回的解密结果**累积标准 CRC-32(多项式 0xEDB88320,查表由 VA 0x01402240 在首次使用时生成),最后判 `~crc == 期望值`;长度不是 4 的倍数直接判失败。两个全局变量各只被读一次,没有静态写入。例程只有 4 个调用点:文件读取两处(VA 0x011ABDF0 按映射、0x011AC310 按 stdio),打开 PVF 两处(0x011AD7F0 映射版、0x011AE7A0 属 stdio 版 0x011AE4B0 的后半)。读取文件时,调用方把目录项的 `crc` **同时**当期望值与密钥传入,CRC 的种子取条目的第一个字段(即 `fileNo`)。**数据验证:全库 334,998 个文件的 `zlib.crc32(解密后按 4 字节对齐的整段, 种子 = fileNo) == crc`;种子取 0 的 0 个通过;目录树 `crc32(整段, 种子 = nFiles) == dirTreeCrc`,目录树末尾那 1 个字节是零填充;所有 253,945 个有填充的文件,解密后的填充字节全是零。** C# 实现(`Crc32`、`PvfArchive.Read(entry, out ok)`、`DirectoryChecksumValid`)与 Python 独立实现各自得到同样结果 | 观察(例程的行为由反汇编读出,校验规则由全库数据验证) | 非 REA(Ghidra + Python + C#)。`tools/ghidra/DumpFunction.java`(反汇编与反编译只写到 `re/notes/`,不入库)、`tools/py/pvf_crc_probe.py` → `out/pvf_crc_probe.json`、`dnf verify pvf` → `out/p1_verification_pvf.json` | `python3 tools/py/pvf_crc_probe.py`(约 5 秒);`dnf verify pvf`(约 2 秒;`directory_checksum_valid = True`、`file_checksum_passes = 334998`) | 高。这是**独立于我们自己实现的旁证**:校验值由原版客户端的算法定义,解密只要有一个字错,对应文件就以 1 − 2⁻³² 的概率校验失败;所以它同时证明了解密实现(含填充处理)与原版一致。局限:只证明数据与算法自洽,不证明"字节语义"(标签含义等)正确;全局变量 B 在运行时是否被赋值**未知** |
| E-012 | **本机 `DNF.exe` 与同目录 `DNF.exe.bak_original` 只有 1 个字节不同**:文件偏移 0xDF870E(VA 0x011F930E,`nsn#0` 内)。原版是 `push -1`,当前是 `push 0`,作为一次经导入地址槽(VA 0x0155A048,Ghidra 解析为 GDI 的 `SetTextColor`)调用的颜色参数:白色改成黑色。这是用户自己环境里的界面改动,**与反作弊、数据格式和逻辑无关** | 观察 | 非 REA。`cmp -l`、PE 节表映射、`tools/ghidra/Peek.java` | `cmp -l DNF.exe DNF.exe.bak_original`;`analyzeHeadless … -postScript Peek.java 0155a048:4` | 高(字节差异);"这是为了界面可读"是推断 |
| E-013 | **PVF 打开有两条路径,本机数据走 stdio 那条**。`fopen` 路径(VA 0x011AE4B0)依次读出:GUID、`version`、`dirTreeLen`、`dirTreeCrc`、`nFiles`,再整块读入目录树并解密校验,条目保存为 60 字节一条的向量(第一个字段是 `fileNo`,其后含明文长度、对齐长度、`crc`、偏移)。文件按"路径转小写、`\` 换成 `/`、折叠 `.` 与 `..`"的规范化后查表(哈希用 djb2 的变体 5381·33+c,再比较字符串)。另一条"内存映射"路径(VA 0x011AD7F0)的头布局不同:它在映射视图的 +0x20 处比较一个 6 字节的明文签名,而本机 `Script.pvf` 的该位置不是这个签名,所以按静态分析本机数据只能走 stdio 路径(推断,没有运行验证) | 观察(读出的流程)/ 推断(为何有两种容器) | 非 REA(Ghidra)。`tools/ghidra/DumpFunction.java`(结果只在 `re/notes/`) | 对 VA 0x011AE4B0、0x011AD7F0、0x011ACB40、0x011ACA20 运行 `DumpFunction.java` | 中高。第二种容器的来历、谁选择哪条路径(`this+0x38` 的标志位由谁设置)**未知**。哈希只是从查找代码里读出的,未与全库路径逐一验证 |

## 服务端(用户自己的服务器,只读勘察)

范围:用户提供了自己的云服务器的 SSH 访问。只读:不改配置、不起停服务、不往服务器写公钥;不读 `.env`、`privatekey.pem`、数据库文件。服务器地址、账号与口令只放 `local.secrets.env`(已 gitignore),不进仓库;提交守卫会拦截这些取值出现在被暂存的文本里。服务端二进制属于逆向产物,只放 `re/`,不提交。

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-014 | **服务端部署**:Ubuntu 24.04,Docker 容器 `dnf`(社区镜像 `llnut/dnf:debian13-qf1031-latest`,`OPEN_CHANNEL` 为 11 与 52)。进程:`df_game_r` 两个(两个频道)、`df_dbmw_r` 三个、`zergsvr`(安全服务)。监听:TCP 5505(与启动器 `Config.example.toml` 里 `server_url` 的端口一致,HTTP)、TCP 7001、7300、30011、30052;UDP 7001、7300、2311–2313、31011、31052;容器内 MySQL 3306 被映射到宿主机的一个公网端口。服务端的 PVF 在容器内是 `/data/Script.pvf`,对应宿主机 `/opt/dnf/data/`。SSH 登录账号有免密码 sudo(勘察里只用它读取 root 拥有的 `/proc` 与目录) | 观察 | 非 REA。`tools/server-recon.sh`、`tools/server-recon2.sh`、`tools/server-recon3.sh` 的输出(`re/server/recon*.txt`,不入库) | `tools/server-ssh.sh 'bash -s' < tools/server-recon.sh`(另两个同理) | 高(直接读到);除 5505 外各端口的用途是**推断**(没有抓包) |
| E-015 | **服务端二进制 `df_game_r` 带完整符号表与 DWARF 调试信息,未剥离**:ELF32 x86,36,360,954 字节。出厂备份 `df_game_r.bak20261008_auction` 的 SHA-256 为 `8859c99d…5261`(已取回到 `re/server/bin/df_game_r.orig`,校验一致);正在运行的是同大小、打过补丁的文件(SHA-256 `37fe1767…f15b`)。符号:`nm` 348,337 条,其中函数类(`T/t/W/w`)119,436 个,不同类名 3,719 个;其中约 6.7 万个是 std/boost 模板与 yaSSL、TaoCrypt、TinyXML 等第三方类的符号,约 1.95 万个是不带类名的自由函数(含静态链接的 OpenSSL、MySQL 客户端等 C 库),其余是游戏自己的类:报文分发类约 400 个(`Dispatcher_*` 与 `DisPatcher_*`,接口 `IPacketDispatcher` 带 1,548 个符号)、`CUser`(1,007 个)、`CParty`、`CInventory`、`CDungeon`、`CSkill`、`SkillSlot`,以及服务端校验类(`Secu_*`、`CHackLog_*`)。10 个调试节存在(`.debug_info` 等) | 观察 | 非 REA。`nm`、`c++filt -n`;服务器上的 `file`、`readelf`。符号清单在 `re/server/symbols/`(不入库) | `tools/server-scp.sh /opt/dnf/data/df_game_r.bak20261008_auction re/server/bin/df_game_r.orig`;`nm re/server/bin/df_game_r.orig \| c++filt -n` | 高(符号表)。**未知**:服务端与客户端共享多少代码;符号名是否与客户端行为一一对应 |
| E-016 | **客户端与服务端的 `Script.pvf` 不是同一份文件**:服务端当前文件 177,683,640 字节(SHA-256 `ac727b2a…0fe6`);服务端出厂备份 `Script.pvf.bak20260915` 与本机客户端的 `Script.pvf` 同为 177,680,913 字节,但 SHA-256 不同(备份 `de1ce98e…b330`,客户端 `fb012d6f…32d0`)。差异的位置与原因**未知**。服务端备份文件名里的 `hell3x`、`card3pct` 等,说明服务端的 PVF 被改过(推断) | 观察(大小与哈希)/ 未知(原因) | 非 REA。`sha256sum`(服务器)、`shasum -a 256`(本机) | `tools/server-ssh.sh 'sha256sum /opt/dnf/data/Script.pvf*'`;`shasum -a 256 "$DNF_DIR/Script.pvf"` | 高(哈希);"用户改过服务端"是推断。**影响**:我们以客户端的 PVF 为准(它是客户端实际读取的数据);需要时按 1 MiB 分块比较哈希定位差异 |

服务端二进制里只有一小部分编译单元带 DWARF(21 个,`.debug_info` 共 660 KB):6 个 FreeType 源文件,以及公司自己的公共库——`FilePack/Pack.cpp`、`DnfLexer/` 下 5 个文件(`DNFlex`、`DNFlexSub`、`DNFLexWrapper`、`CompiledDNFLex`、`ScriptStringManager`)、`Core/` 下 8 个文件(`Crc32`、`Lock`、`SecureStdio`、`Strings`、`Tick`、`UnicodeConvert`、`XorRand`、`CountryCode`)。游戏逻辑类(报文、技能、副本等)只有符号名,没有类型信息。编译器 GCC 4.4.6(Red Hat)。

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-017 | **脚本记号类型的独立印证**:DWARF 里词法器的记号枚举共 12 项,与 E-003 凭字节形状推出的类型一一对应:2 十进制整数、3 十六进制整数、4 浮点、5 类型(标签)、6 自定义数据、7 字符串、8 连接符、9 整数对、10 字符串对,另有 0 结束、1 错误、11 include(12 为上限)。没有冲突项。类型 11 在全库编译后的流里没有出现 | 观察 | 非 REA。`tools/py/dwarf_dump.py` → `re/server/dwarf/Include_DnfLexer_CompiledDNFLex.cpp.txt`(不入库) | `.venv/bin/python tools/py/dwarf_dump.py re/server/bin/df_game_r.orig re/server/dwarf 'FilePack\|DnfLexer\|Core/'` | 高(枚举取值)。"客户端的词法器与它同源"是推断,依据是两者对同一份数据格式一致 |
| E-018 | **PVF 打包库的函数与类型名**:打包库里有"解密并校验 CRC-32"函数,参数依次是种子、期望校验值、密钥、缓冲区、长度;另有加密、路径哈希、路径规范化函数;索引项(28 字节)含类型、名字、长度、对齐后长度、`crc`、偏移、内容指针;打包库对象含 `revision`(对应头里的 `version`)、头长度、加载类型(全部加载 / 仅索引);返回码有"标签不匹配"与"内容损坏"等。这与 E-011 从客户端 VA 0x011AB180 读出的用法一致(种子取 `fileNo`、期望值与密钥都取 `crc`)。公共库里还有 CRC-32 表生成函数、`XorRand4/8` 随机数类 | 观察(服务端符号)/ 推断(客户端对应) | 非 REA。同 E-017 | 同 E-017 | 高(服务端符号与类型);客户端对应关系是推断,但被 E-011 的全库验证支持 |
| E-019 | **服务端二进制认识约八成的脚本标签**:服务端里形如 `[标签]` 的字符串共 3,746 个(3,602 个被代码引用)。与全库 3,408 种开标签精确匹配(含方括号):2,143 种(62.9%)有引用,按出现次数加权 2,608,305 / 3,232,710 = **80.7%**。服务端加载的数据类型几乎全覆盖:`.equ` 99.6%、`.stk` 100%、`.atk` 99.2%、`.mob` 99.6%、`.map` 100%、`.qst` 100%、`.obj` 100%、`.skl` 99.7%、`.dgn` 100%、`.aic` 99.9%、`.npc` 100%、`.cre` 100%、`.chr` 100%、`.til` 100%;只有客户端用的类型很低:`.act` 1.1%(19,079 个文件、605 种标签)、`.ptl` 12.4%、`.ui` 12.8%、`.wrd` 17.2%、`.ai` 41.3%、`.als` 0%。服务端不认识、出现最多的标签几乎都是动作与 AI 脚本的语句(`[TRIGGER]`、`[DO BEHAVIOR]`、`[BEHAVIOR]`、`[FRAME]`、`[MOTION]`、`[BASE ANI]`、`[PARTICLE FILENAME]` 等) | 观察 | 非 REA(Ghidra)。`tools/ghidra/TagRefs.java` → `re/ghidra/out/server_tag_refs.tsv`;`tools/py/server_tag_join.py` → `out/server_tag_join.json`(都不入库) | `analyzeHeadless re/ghidra/projects dfgame -process df_game_r.orig -noanalysis -readOnly -scriptPath tools/ghidra -postScript TagRefs.java re/ghidra/out/server_tag_refs.tsv`,再 `.venv/bin/python tools/py/server_tag_join.py` | 高(字符串精确匹配 + 交叉引用)。局限:"服务端认识这个标签"不等于"语义已知",还要读加载函数;方括号字符串偶尔在别处被当文本用;服务端的解释与客户端不一定完全一致 |
| E-020 | **脚本类型与服务端加载函数的对应**(取引用不同标签数最多的函数):`.equ`→`ImportEquipmentScript`(133 种标签)、`.stk`→`ImportStackableScript`(139)、`.atk`→`ImportAttackInfoScript`(69)、`.mob`→`ImportMonsterScript`(181)、`.map`→`ImportMapScript`(86)、`.qst`→`importQuestScript`(62)、`.obj`→`ImportPassiveObjectScript`(89)、`.skl`→`ImportSkillScript`(58)、`.dgn`→`ImportDungeonScript`(153)、`.aic`→`importAICharacterCommonScript`(50)、`.npc`→`importNpcScript`(47)、`.cre`→`importCreatureScript`(84)、`.chr`→`ImportCharacterScript`(71)、`.til`→`ImportTileScript`(4)、`.etc`→`importServerParameterScript`(116)。这些函数是逐标签读出语义的入口(参数是该类脚本的结构体指针) | 观察(函数名与标签集合) / 推断(函数与文件类型的对应) | 同 E-019 | 同 E-019 | 中高:名字来自符号表,对应关系由标签集合推断,没读函数体 |

## 原版客户端基线(运行观察,不是对 DNF.exe 的调试或分析)

| ID | 结论 | 类型 | 来源与产物 | 复现 | 置信度 |
|---|---|---|---|---|---|
| E-022 | **原版客户端能在 CrossOver 26.1(macOS 15.7、arm64)下连用户自己的服务器并进入城镇**:启动器取令牌并启动 `DNF.exe`(启动器日志"Launching DNF with authentication token""DNF launched");代理 DLL 日志显示 `start.dnf.tw` 被指到用户的服务器;城镇画面里 HUD、NPC、传送装置、聊天栏都正常渲染,窗口内容 800×600。第一张对照截图 `re/baseline/runs/20261014-113526/shots/02-client-window.png`(不入库)。进游戏后的操作由用户手动完成(我没有"辅助功能"授权) | 观察 | 非 REA。`tools/baseline/`(启动封装与最小权限截图小工具);运行记录 `re/baseline/runs/20261014-113526/{run-info.txt,wine.log}`(不入库) | `tools/baseline/launch-dnf.sh`(后台),在启动器里点"进入游戏",然后 `tools/baseline/tool.sh shot --owner DNF.exe --title "DNF Taiwan" --out <文件>.png` | 高(只是"能跑到城镇");这不是逐场景基线,场景清单与阈值见 `docs/baseline.md`,只采了 S03 的 1 张 |
| E-023 | **IMG 像素解码与原版客户端的屏幕输出逐像素一致,前提是 1555 的 5 位通道按 `round(v*255/31)` 展开,而不是位复制。**在 E-022 的城镇截图(800×600,窗口内容 1:1)里,用 `dnf locate` 在全部 4,999,480 个 IMG 帧里找"不透明像素与截图完全相同"的帧,容差 0、一致比例 ≥ 99%,得到 33 个匹配,位置各不相同的精灵有邮筒、法师小人、技能与状态图标、底部 HUD 条、任务面板等。其中 9 个 ARGB1555 帧共 10,439 个不透明像素:按 round 展开 **0** 个不一致,按位复制展开 2,103 个不一致(20%,单帧 12% 到 36%);24 个 ARGB8888 帧共 23,264 个不透明像素:19 个帧完全一致,另 5 个帧共 19 个像素不一致(HUD 右上角一帧 7 个像素,通道差最大 89,像是被别的元素盖住;gunner 项链的 4 个同图帧各 3 个像素,差 ≤ 7,原因未明)。据此更正了 `ImgPixels`(C#)与 `dnfdata.py`,并新增会区分两种规则的测试向量(xunit、Unity EditMode、Unity 播放器冒烟)。受影响的是 ARGB1555 帧:1,759,800 个,占全库非链接帧的 17.8% | 观察 | 非 REA。`src/Dnf.Cli/Analysis/SpriteLocator.cs`(`dnf locate`)、`tools/py/screen_match_check.py`(独立的 Python 复核,同时比较两种规则);原版截图 `re/baseline/runs/20261014-113526/shots/02-client-window.png`(不入库);输出 `out/locate_after_fix.json`、`out/screen_match_check.json`(只有数字) | `dotnet src/Dnf.Cli/bin/Release/net10.0/dnf.dll locate --shot <截图> --min-ratio 0.99 --json out/locate_after_fix.json`(约 19 秒);`python3 tools/py/screen_match_check.py --shot <截图> --locate out/locate_after_fix.json`(< 1 秒) | 中高:两条独立路径(C# 与 Python)对同一批像素给出同一结论,且 0 对 2,103 的差别很大;**但**只覆盖 alpha=255 的像素、一个场景、一台机器(CrossOver 的渲染路径,不是原生 Windows)。没有 ARGB4444 样本(全库只有 7,800 帧);半透明、染色、缩放和叠加类像素不在范围内;原版里这个展开是客户端代码做的还是显卡做的,没有分析(留给 P6) |

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
