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
//   <地址>.cl.txt     同一份反编译,逐行带上"这一行对应的最低指令地址"(每行:8 位十六进制地址、制表符、缩进后的文本;
//                     没有地址的行写 00000000),用来把 C 文本按汇编地址切段(比如按类型分派链的处理块切)
//   <地址>.xref.txt   调用者(谁调用了它)与被调用者(它调用了谁)的地址
import ghidra.app.decompiler.ClangLine;
import ghidra.app.decompiler.ClangToken;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.decompiler.PrettyPrinter;
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

    // 受保护区域的虚拟地址范围(节 #3 到 #8,见 docs/evidence/ledger.md 的 E-044);可用环境变量覆盖(十六进制)
    private long protectedLo = 0x01dd9000L;
    private long protectedHi = 0x02c98000L;

    private boolean isProtected(Address a) {
        long v = a.getOffset();
        return v >= protectedLo && v < protectedHi;
    }


    /** 一条非调用指令可能流向的地址:getFlows() 加上操作数里直接写出的地址(有的跳转没有建引用)。 */
    private java.util.List<Address> jumpTargets(Instruction ins) {
        java.util.List<Address> out = new java.util.ArrayList<>();
        Address[] flows = ins.getFlows();
        if (flows != null) {
            for (Address t : flows) {
                if (t != null) out.add(t);
            }
        }
        if ((ins.getFlowType().isJump() || ins.getFlowType().isTerminal()) && ins.getNumOperands() > 0) {
            Address a0 = ins.getAddress(0);
            if (a0 != null) out.add(a0);
            for (Object o : ins.getOpObjects(0)) {
                if (o instanceof Address) {
                    out.add((Address) o);
                } else if (o instanceof ghidra.program.model.scalar.Scalar) {
                    long v = ((ghidra.program.model.scalar.Scalar) o).getUnsignedValue();
                    out.add(ins.getAddress().getNewAddress(v));
                }
            }
        }
        return out;
    }

    /** 返回拒绝原因;null 表示可以处理。 */
    private String protectedReason(Function f) {
        if (isProtected(f.getEntryPoint())) {
            return "入口在受保护区域";
        }
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            // 普通调用只是引用地址,反编译器不会把被调用者并进来;但"调用并终止"(尾跳,如跳板)要算
            if (ins.getFlowType().isCall() && !ins.getFlowType().isTerminal()) {
                continue;
            }
            for (Address t : jumpTargets(ins)) {
                if (isProtected(t)) {
                    return "在 " + ins.getAddress() + " 跳进受保护区域";
                }
            }
        }
        return null;
    }

    @Override
    protected void run() throws Exception {
        String lo = System.getenv("DNF_PROTECTED_LO");
        String hi = System.getenv("DNF_PROTECTED_HI");
        if (lo != null && !lo.isEmpty()) {
            protectedLo = Long.parseLong(lo.trim(), 16);
        }
        if (hi != null && !hi.isEmpty()) {
            protectedHi = Long.parseLong(hi.trim(), 16);
        }
        // 2026-10-15 用户授权逆向受保护区域(E-059):显式设 DNF_ALLOW_PROTECTED=1 才关掉守卫
        if ("1".equals(System.getenv("DNF_ALLOW_PROTECTED"))) {
            protectedLo = 0L;
            protectedHi = 0L;
        }
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: DumpFunction.java <输出目录> <函数地址> [<函数地址> ...]");
            return;
        }
        File outDir = new File(args[0]);
        outDir.mkdirs();
        FunctionManager fm = currentProgram.getFunctionManager();

        // 反编译的超时秒数,默认 120;很大的函数(几万字节)可以用环境变量 DNF_DECOMPILE_TIMEOUT 调大
        int decompileTimeout = 120;
        String timeoutText = System.getenv("DNF_DECOMPILE_TIMEOUT");
        if (timeoutText != null && !timeoutText.isEmpty()) {
            decompileTimeout = Integer.parseInt(timeoutText.trim());
        }
        // 只要反汇编与调用关系、不要反编译(几万字节的加载函数反编译要几分钟):DNF_SKIP_DECOMPILE=1
        boolean skipDecompile = "1".equals(System.getenv("DNF_SKIP_DECOMPILE"));
        // 反编译结果的最大载荷(MB),默认用 Ghidra 的设置;大函数会报"Response buffer size exceeded",用 DNF_DECOMPILE_PAYLOAD_MB 调大
        String payloadText = System.getenv("DNF_DECOMPILE_PAYLOAD_MB");
        DecompInterface decomp = new DecompInterface();
        if (payloadText != null && !payloadText.isEmpty()) {
            DecompileOptions options = new DecompileOptions();
            options.setMaxPayloadMBytes(Integer.parseInt(payloadText.trim()));
            decomp.setOptions(options);
        }
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

                // 受保护区域守卫:函数本身在受保护区域,或者有"跳"(不是调用)进受保护区域的指令(跳板、尾跳),
                // 一律拒绝:反编译器会顺着跳转把那里的字节当作本函数的一部分读进来。
                String why = protectedReason(f);
                if (why != null) {
                    printerr("拒绝(受保护区域): " + tag + " " + why);
                    continue;
                }

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

                DecompileResults res = null;
                if (!skipDecompile) {
                    res = decomp.decompileFunction(f, decompileTimeout, monitor);
                    try (PrintWriter w = new PrintWriter(new File(outDir, tag + ".c.txt"), "UTF-8")) {
                        if (res != null && res.decompileCompleted() && res.getDecompiledFunction() != null) {
                            w.println(res.getDecompiledFunction().getC());
                        } else {
                            w.println("// 反编译失败: " + (res == null ? "无结果" : res.getErrorMessage()));
                        }
                    }
                }
                if (res != null && res.decompileCompleted() && res.getCCodeMarkup() != null) {
                    try (PrintWriter w = new PrintWriter(new File(outDir, tag + ".cl.txt"), "UTF-8")) {
                        PrettyPrinter pp = new PrettyPrinter(f, res.getCCodeMarkup(), null);
                        for (ClangLine cl : pp.getLines()) {
                            long min = Long.MAX_VALUE;
                            StringBuilder sb = new StringBuilder();
                            for (int k = 0; k < cl.getIndent(); k++) {
                                sb.append(' ');
                            }
                            for (ClangToken t : cl.getAllTokens()) {
                                sb.append(t.getText());
                                Address ta = t.getMinAddress();
                                if (ta != null && ta.getOffset() < min) {
                                    min = ta.getOffset();
                                }
                            }
                            w.println(String.format("%08x\t%s", min == Long.MAX_VALUE ? 0L : min, sb));
                        }
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
