// 把"自己起的名字与原型"应用到 Ghidra 工程(副本),让反编译结果读起来更清楚。
// 名字只是读代码时的路标,来自客户端自己的日志字符串(函数名)或自己对行为的命名;不改程序字节。
// 只应对工程的"副本"运行(会改工程);原始的 dnffix 工程不动。
//
// 输入文件(TSV,UTF-8,# 开头为注释):
//   <地址>\t<名字>\t<可选的 C 原型>\t<可选的调用约定,如 __thiscall>
// 原型例:void vectorPushStub(int *value)    (函数名要与第二列一致;this 由调用约定 __thiscall 自动补上)
//
// 用法(脚本参数):<TSV 路径>
//   analyzeHeadless re/ghidra/projects dnfparam -process DNF.exe -noanalysis \
//       -scriptPath tools/ghidra -postScript ApplyNames.java tools/ghidra/names/act.tsv
import ghidra.app.cmd.function.ApplyFunctionSignatureCmd;
import ghidra.app.script.GhidraScript;
import ghidra.app.util.parser.FunctionSignatureParser;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.FunctionDefinitionDataType;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileReader;

public class ApplyNames extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("用法: ApplyNames.java <TSV 路径>");
            return;
        }
        int named = 0, sigs = 0, failed = 0;
        try (BufferedReader r = new BufferedReader(new FileReader(new File(args[0])))) {
            String line;
            while ((line = r.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) continue;
                String[] p = line.split("\t");
                if (p.length < 2) continue;
                Address a = toAddr(p[0].trim());
                Function f = currentProgram.getFunctionManager().getFunctionAt(a);
                if (f == null) {
                    println("没有函数: " + p[0]);
                    failed++;
                    continue;
                }
                try {
                    f.setName(p[1].trim(), SourceType.USER_DEFINED);
                    named++;
                } catch (Exception e) {
                    println("改名失败 " + p[0] + ": " + e.getMessage());
                    failed++;
                    continue;
                }
                if (p.length >= 3 && !p[2].trim().isEmpty()) {
                    try {
                        FunctionSignatureParser parser =
                            new FunctionSignatureParser(currentProgram.getDataTypeManager(), null);
                        FunctionDefinitionDataType def = parser.parse(null, p[2].trim());
                        ApplyFunctionSignatureCmd cmd =
                            new ApplyFunctionSignatureCmd(a, def, SourceType.USER_DEFINED, false, true);
                        if (cmd.applyTo(currentProgram, monitor)) {
                            sigs++;
                            if (p.length >= 4 && !p[3].trim().isEmpty()) {
                                f.setCallingConvention(p[3].trim());
                            }
                        } else {
                            println("原型应用失败 " + p[0] + ": " + cmd.getStatusMsg());
                            failed++;
                        }
                    } catch (Exception e) {
                        println("原型解析失败 " + p[0] + ": " + e.getMessage());
                        failed++;
                    }
                }
            }
        }
        println("已命名 " + named + " 个,应用原型 " + sigs + " 个,失败 " + failed + " 个");
    }
}
