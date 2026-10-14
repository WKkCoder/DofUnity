# native/squirrel

Squirrel 3.2 的源码子集,**逐字节未修改**。

- 来源:<https://github.com/albertodemichelis/squirrel>,标签 `v3.2`,提交 `f92bc298784ceea459b12e2de33bdff672bfeb83`(2022-02-10)。
- 许可:MIT,全文在 `COPYRIGHT`。
- 只带虚拟机需要的部分:`squirrel/`(编译器与虚拟机)、`sqstdlib/` 里的 `sqstdaux`、`sqstdmath`、`sqstdstring`、`sqstdrex`,以及对应头文件。没有带 `sq` 命令行、`sqstdio`、`sqstdblob`、`sqstdsystem`(脚本不应该读写文件或调系统命令)。
- 编译选项见 `tools/build-squirrel.sh`:`-D_SQ64`(整数 64 位、浮点 32 位)、`-fno-exceptions -fno-rtti`。
- 与上游的比较:用 `cmp` 逐文件对照上游检出(`re/third_party/squirrel-3.2`,不入库),没有差异。

为什么选 3.2:用户自备数据里的 212 个明文 `.nut` 脚本在 3.2 下全部能编译(证据 E-033)。原版 `DNF.exe` 内嵌的 Squirrel 的版本**未知**(推断:2.x 系列),所以 3.2 与原版在运行语义上的差异(整数宽度、个别库函数、`_get` 委托行为)没有被验证。

宿主封装在 `native/dnfsq/dnfsq.cpp`(我们自己的代码),C# 侧在 `packages/com.dnfunity.squirrel`。
