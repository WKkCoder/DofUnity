// 把指定函数的反汇编、反编译结果与调用关系写到文件,供分析者本地阅读。
// 输出只放在 re/(已被 .gitignore 与提交守卫排除),绝不提交;进台账的只有用自己的话写的结论与地址。
//
// 用法(脚本参数):<输出目录> <函数地址 1> [<函数地址 2> ...]
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript DumpFunction.java re/notes 011ab180
//
// 每个函数产出三个文件:
//   <地址>.asm.txt    反汇编
//   <地址>.c.txt      反编译(Ghidra 的自动命名,仅供阅读)
//   <地址>.xref.txt   调用者(谁调用了它)与被调用者(它调用了谁)的地址
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import java.io.File;
import java.io.PrintWriter;
import java.util.Map;
import java.util.TreeMap;

public class DumpFunction extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: DumpFunction.java <输出目录> <函数地址> [<函数地址> ...]");
            return;
        }
        File outDir = new File(args[0]);
        outDir.mkdirs();
        FunctionManager fm = currentProgram.getFunctionManager();

        DecompInterface decomp = new DecompInterface();
        decomp.openProgram(currentProgram);
        try {
            for (int i = 1; i < args.length; i++) {
                Address entry = toAddr(args[i]);
                Function f = fm.getFunctionAt(entry);
                if (f == null) {
                    f = fm.getFunctionContaining(entry);
                }
                if (f == null) {
                    printerr("没有函数: " + args[i]);
                    continue;
                }
                String tag = f.getEntryPoint().toString();

                try (PrintWriter w = new PrintWriter(new File(outDir, tag + ".asm.txt"), "UTF-8")) {
                    w.println("; 函数入口 " + tag + ",大小 " + f.getBody().getNumAddresses() + " 字节");
                    InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
                    while (it.hasNext()) {
                        Instruction ins = it.next();
                        StringBuilder bytes = new StringBuilder();
                        for (byte b : ins.getBytes()) {
                            bytes.append(String.format("%02x", b & 0xff));
                        }
                        w.println(String.format("%s  %-20s %s", ins.getAddress(), bytes, ins));
                    }
                }

                try (PrintWriter w = new PrintWriter(new File(outDir, tag + ".c.txt"), "UTF-8")) {
                    DecompileResults res = decomp.decompileFunction(f, 120, monitor);
                    if (res != null && res.decompileCompleted() && res.getDecompiledFunction() != null) {
                        w.println(res.getDecompiledFunction().getC());
                    } else {
                        w.println("// 反编译失败: " + (res == null ? "无结果" : res.getErrorMessage()));
                    }
                }

                try (PrintWriter w = new PrintWriter(new File(outDir, tag + ".xref.txt"), "UTF-8")) {
                    Map<String, Integer> callers = new TreeMap<>();
                    ReferenceIterator refs = currentProgram.getReferenceManager().getReferencesTo(f.getEntryPoint());
                    while (refs.hasNext()) {
                        Reference r = refs.next();
                        Function cf = fm.getFunctionContaining(r.getFromAddress());
                        String key = (cf == null ? "-" : cf.getEntryPoint().toString()) + " <- " + r.getFromAddress() + " " + r.getReferenceType();
                        callers.merge(key, 1, Integer::sum);
                    }
                    w.println("# 调用者(函数入口 <- 调用点 类型):" + callers.size());
                    for (String k : callers.keySet()) {
                        w.println(k);
                    }
                    Map<String, Integer> callees = new TreeMap<>();
                    InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
                    while (it.hasNext()) {
                        Instruction ins = it.next();
                        if (!ins.getFlowType().isCall()) {
                            continue;
                        }
                        for (Reference r : ins.getReferencesFrom()) {
                            if (r.getReferenceType().isCall()) {
                                Function cf = fm.getFunctionAt(r.getToAddress());
                                String key = r.getToAddress() + (cf == null ? " (无函数)" : (cf.isThunk() ? " (thunk)" : ""));
                                callees.merge(key, 1, Integer::sum);
                            }
                        }
                    }
                    w.println("# 被调用者:" + callees.size());
                    for (Map.Entry<String, Integer> e : callees.entrySet()) {
                        w.println(e.getKey() + " x" + e.getValue());
                    }
                }
                println("已写出 " + tag);
            }
        } finally {
            decomp.dispose();
        }
    }
}
