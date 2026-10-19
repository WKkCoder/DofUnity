// 找出"切换状态"的调用点,并标出被切换到的状态号。
// 客户端的 setState(状态号, 数据向量, 参数3, 参数4, 参数5) 是 __thiscall、参数从右往左压栈,
// 所以调用前最后一条 PUSH 就是状态号。这里只做静态匹配,命中里可能混进别的函数(要人工看上下文):
//   (1) 通过虚函数槽调用:CALL dword ptr [REG + 0x槽位移],或 MOV REG2,[REG + 0x槽位移] 之后 CALL REG2;
//   (2) 直接调用某个入口:CALL 0x入口。
// 对每个调用点往回看最多 18 条指令,取离调用最近的一条 PUSH,列出它的操作数(立即数就是状态号)。
//
// 用法(脚本参数):<输出文件> <槽位移十六进制|-> <直接入口十六进制|-> [<只保留的状态号十六进制> ...]
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript FindStateCalls.java out/hero/state_calls.tsv 30c 008779b0 e f
//
// 输出 TSV:form, function, call_address, push_operand, push_address。只读静态,不运行程序。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.io.File;
import java.io.PrintWriter;
import java.util.HashSet;
import java.util.Set;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class FindStateCalls extends GhidraScript {

    private static final int LOOK_BACK = 18;

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("用法: FindStateCalls.java <输出文件> <槽位移|-> <直接入口|-> [<状态号> ...]");
            return;
        }
        String slot = args[1].equals("-") ? null : args[1].toLowerCase().replaceFirst("^0x", "");
        String entry = args[2].equals("-") ? null : args[2].toLowerCase().replaceFirst("^0x", "");
        Set<Long> keep = new HashSet<>();
        for (int i = 3; i < args.length; i++) {
            keep.add(Long.parseLong(args[i].toLowerCase().replaceFirst("^0x", ""), 16));
        }
        Pattern direct = slot == null ? null
                : Pattern.compile("^CALL dword ptr \\[[A-Z]{3} \\+ 0x" + slot + "\\]");
        Pattern load = slot == null ? null
                : Pattern.compile("^MOV ([A-Z]{3}),dword ptr \\[[A-Z]{3} \\+ 0x" + slot + "\\]$");
        Pattern callEntry = entry == null ? null
                : Pattern.compile("^CALL 0x0*" + entry + "$");
        Pattern pushImm = Pattern.compile("^PUSH (-?0x[0-9a-f]+)$");
        int hits = 0;
        try (PrintWriter w = new PrintWriter(new File(args[0]), "UTF-8")) {
            w.println("form\tfunction\tcall_address\tpush_operand\tpush_address");
            InstructionIterator it = currentProgram.getListing().getInstructions(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Instruction ins = it.next();
                String s = ins.toString();
                String form = null;
                Instruction callIns = null;
                if (direct != null && direct.matcher(s).find()) {
                    form = "direct";
                    callIns = ins;
                } else if (callEntry != null && callEntry.matcher(s).find()) {
                    form = "entry";
                    callIns = ins;
                } else if (load != null) {
                    Matcher m = load.matcher(s);
                    if (m.find()) {
                        String reg = m.group(1);
                        Instruction nx = ins.getNext();
                        for (int k = 0; k < 8 && nx != null; k++, nx = nx.getNext()) {
                            String t = nx.toString();
                            if (t.equals("CALL " + reg) || t.equals("JMP " + reg)) {
                                form = "loaded";
                                callIns = nx;
                                break;
                            }
                            if (t.startsWith("MOV " + reg + ",") || t.startsWith("XOR " + reg + ",")
                                    || t.startsWith("POP " + reg)) {
                                break;
                            }
                        }
                    }
                }
                if (callIns == null) {
                    continue;
                }
                String pushOperand = "-";
                String pushAddr = "-";
                Instruction prev = callIns.getPrevious();
                for (int k = 0; k < LOOK_BACK && prev != null; k++, prev = prev.getPrevious()) {
                    String t = prev.toString();
                    if (t.startsWith("PUSH ")) {
                        pushOperand = t.substring(5);
                        pushAddr = prev.getAddress().toString();
                        break;
                    }
                }
                if (!keep.isEmpty()) {
                    Matcher m = pushImm.matcher("PUSH " + pushOperand);
                    if (!m.find()) {
                        continue;
                    }
                    long v = Long.decode(m.group(1));
                    if (!keep.contains(v)) {
                        continue;
                    }
                }
                Function f = getFunctionContaining(callIns.getAddress());
                w.println(form + "\t" + (f == null ? "-" : f.getEntryPoint().toString()) + "\t"
                        + callIns.getAddress() + "\t" + pushOperand + "\t" + pushAddr);
                hits++;
            }
        }
        println("命中 " + hits + " 条");
    }
}
