// 补全 Ghidra 没有解出来的"跳转表"(switch 语句),让大函数的函数体与反编译结果完整。
//
// 背景(E-044):DNF.exe 里有不少大函数用 JMP dword ptr [索引*4 + 表地址] 做 switch,Ghidra 没有解出表项,
// 结果各个 case 的代码变成"不属于任何函数的孤立指令"(例如入口 0x00BE1B90 的函数有 52 个 case,
// 表在 0x00BF30E8,case 代码在 0x00BE34F2 到 0x00BEFAAD,都不在函数体里)。反编译这样的函数只会得到前半段。
//
// 做法(只识别最常见的一种写法,其余只计数不处理):
//   ... CMP 索引寄存器, 上界 ; JA 默认分支 ; JMP dword ptr [索引寄存器*4 + 表地址]
//   表项数 = 上界 + 1;逐项读出目标地址,要求全部落在同一个可执行内存块内且离跳转指令不远,
//   然后给跳转指令加上"计算跳转"引用、对目标处反汇编,最后把受影响的函数重建一遍(函数体沿新引用扩展)。
//   重复若干轮,因为新反汇编出来的 case 代码里可能还有嵌套的 switch。
//
// 只读/写入由命令行决定:
//   统计(不改动,不保存):   analyzeHeadless ... -readOnly -postScript FixSwitches.java <报告文件>
//   应用并保存到项目:        analyzeHeadless ... (不带 -readOnly)  -postScript FixSwitches.java <报告文件> apply
// 约定:应用只在项目 dnffix 上做(它是 dnf 项目的一份副本),原始项目 dnf 保持不动。
//
// 脚本参数:<报告文件> [apply] [最大轮数,缺省 4]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.RefType;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.SourceType;
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class FixSwitches extends GhidraScript {

    private static final Pattern JMP_TABLE =
        Pattern.compile("^JMP dword ptr \\[(\\w+)\\*0x4 \\+ (0x[0-9a-fA-F]+)\\]$");
    private static final Pattern CMP_IMM =
        Pattern.compile("^CMP (\\w+),(-?0x[0-9a-fA-F]+)$");
    private static final long MAX_ENTRIES = 4096;
    private static final long MAX_DISTANCE = 0x400000; // 目标离跳转指令最多 4 MiB

    private int total;
    private int resolved;
    private int notTablePattern;
    private int noBound;
    private int badTable;
    private int patched;
    private long entriesAdded;

    private boolean isCode(Address a) {
        MemoryBlock b = currentProgram.getMemory().getBlock(a);
        return b != null && b.isExecute() && b.isInitialized();
    }

    private long boundBefore(Instruction jmp, String idxReg) {
        Instruction p = jmp.getPrevious();
        // 往回最多看 8 条:找 "CMP idxReg,上界" 后面紧跟 JA / JNBE / JAE 之类的无符号越界跳转
        for (int i = 0; i < 8 && p != null; i++, p = p.getPrevious()) {
            Matcher m = CMP_IMM.matcher(p.toString());
            if (m.matches() && m.group(1).equalsIgnoreCase(idxReg)) {
                Instruction nx = p.getNext();
                if (nx != null) {
                    String mn = nx.getMnemonicString();
                    if (mn.equals("JA") || mn.equals("JNBE") || mn.equals("JNC") || mn.equals("JAE")) {
                        long v = Long.decode(m.group(2));
                        return mn.equals("JA") || mn.equals("JNBE") ? v + 1 : v;
                    }
                }
                return -1;
            }
            if (p.getFlowType().isJump() && i > 0 && !p.getFlowType().isConditional()) {
                return -1;
            }
        }
        return -1;
    }

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("用法: FixSwitches.java <报告文件> [apply] [最大轮数]");
            return;
        }
        boolean apply = args.length > 1 && args[1].equals("apply");
        int rounds = args.length > 2 ? Integer.parseInt(args[2]) : 4;
        File out = new File(args[0]);
        out.getParentFile().mkdirs();
        Listing listing = currentProgram.getListing();
        Set<Address> touchedFunctions = new LinkedHashSet<>();
        try (PrintWriter pw = new PrintWriter(out, "UTF-8")) {
            pw.println("round\tjump\tfunction\tstatus\tentries\ttable");
            for (int round = 1; round <= rounds; round++) {
                total = resolved = notTablePattern = noBound = badTable = patched = 0;
                List<Instruction> todo = new ArrayList<>();
                InstructionIterator it = listing.getInstructions(true);
                while (it.hasNext()) {
                    Instruction ins = it.next();
                    if (!ins.getFlowType().isJump() || !ins.getFlowType().isComputed()) {
                        continue;
                    }
                    total++;
                    // 已有两个以上的"计算跳转"目标才算解出来了;只有一个目标的多半是分析器的猜测,仍交给下面按表补全
                    int computedRefs = 0;
                    for (Reference r : ins.getReferencesFrom()) {
                        if (r.getReferenceType().isJump() && r.getReferenceType().isComputed()) {
                            computedRefs++;
                        }
                    }
                    if (computedRefs >= 2) {
                        resolved++;
                        continue;
                    }
                    todo.add(ins);
                }
                int changedThisRound = 0;
                for (Instruction jmp : todo) {
                    Matcher m = JMP_TABLE.matcher(jmp.toString());
                    Function f = listing.getFunctionContaining(jmp.getAddress());
                    String fname = f == null ? "-" : f.getEntryPoint().toString();
                    if (!m.matches()) {
                        notTablePattern++;
                        pw.println(round + "\t" + jmp.getAddress() + "\t" + fname + "\tother_pattern\t0\t" + jmp.toString());
                        continue;
                    }
                    String idxReg = m.group(1);
                    long table = Long.decode(m.group(2)) & 0xffffffffL;
                    long n = boundBefore(jmp, idxReg);
                    if (n <= 0 || n > MAX_ENTRIES) {
                        noBound++;
                        pw.println(round + "\t" + jmp.getAddress() + "\t" + fname + "\tno_bound\t0\t" + Long.toHexString(table));
                        continue;
                    }
                    List<Address> targets = new ArrayList<>();
                    boolean ok = true;
                    for (long i = 0; i < n && ok; i++) {
                        Address ta;
                        try {
                            long v = getInt(toAddr(table + 4 * i)) & 0xffffffffL;
                            ta = toAddr(v);
                        } catch (Exception e) {
                            ok = false;
                            break;
                        }
                        if (ta == null || !isCode(ta) || Math.abs(ta.getOffset() - jmp.getAddress().getOffset()) > MAX_DISTANCE) {
                            ok = false;
                            break;
                        }
                        targets.add(ta);
                    }
                    if (!ok || targets.isEmpty()) {
                        badTable++;
                        pw.println(round + "\t" + jmp.getAddress() + "\t" + fname + "\tbad_table\t" + n + "\t" + Long.toHexString(table));
                        continue;
                    }
                    if (apply) {
                        for (Address ta : targets) {
                            currentProgram.getReferenceManager().addMemoryReference(
                                jmp.getAddress(), ta, RefType.COMPUTED_JUMP, SourceType.ANALYSIS, 0);
                            if (listing.getInstructionAt(ta) == null) {
                                disassemble(ta);
                            }
                        }
                        if (f != null) {
                            touchedFunctions.add(f.getEntryPoint());
                        }
                        entriesAdded += targets.size();
                        changedThisRound++;
                    }
                    patched++;
                    pw.println(round + "\t" + jmp.getAddress() + "\t" + fname + "\t" + (apply ? "patched" : "patchable") + "\t" + targets.size() + "\t" + Long.toHexString(table));
                }
                println("ROUND " + round + " computed_jumps=" + total + " already_resolved=" + resolved
                    + " patchable=" + patched + " other_pattern=" + notTablePattern + " no_bound=" + noBound
                    + " bad_table=" + badTable);
                if (!apply || changedThisRound == 0) {
                    break;
                }
            }
            if (apply) {
                int rebuilt = 0;
                int fail = 0;
                for (Address entry : touchedFunctions) {
                    Function f = listing.getFunctionAt(entry);
                    if (f == null) {
                        continue;
                    }
                    String name = f.getName();
                    long before = f.getBody().getNumAddresses();
                    removeFunction(f);
                    Function g = createFunction(entry, name);
                    if (g == null) {
                        fail++;
                        pw.println("rebuild\t" + entry + "\t-\tfailed\t0\t-");
                    } else {
                        rebuilt++;
                        pw.println("rebuild\t" + entry + "\t" + entry + "\t" + before + "->" + g.getBody().getNumAddresses() + "\t0\t-");
                    }
                }
                println("REBUILT functions=" + rebuilt + " failed=" + fail + " table_entries=" + entriesAdded);
            }
        }
    }
}
