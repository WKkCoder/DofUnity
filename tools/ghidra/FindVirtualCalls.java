// 找出"调用某个虚函数槽"的地方:按槽的位移(槽号 × 4)查两种写法——
//   (1) CALL dword ptr [REG + 0x位移];
//   (2) MOV REG2,dword ptr [REG + 0x位移],随后几条指令内 CALL REG2(或 JMP REG2)。
// 用途:知道一个虚函数是哪个槽(见 tools/py/vtable_scan.py / re/notes/rtti_classes.tsv),想知道谁在通过虚表调用它。
// 命中里会混进"别的类的字段恰好在同一个位移上读出来又调用"的情况,需要人工用上下文排除。
//
// 用法(脚本参数):<输出文件> <十六进制位移> [<十六进制位移> ...]
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript FindVirtualCalls.java out/combat/vcalls.tsv 2dc 2d0
//
// 输出 TSV:offset, form(direct/loaded), function, call_address, instruction。只读静态,不运行程序。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.io.File;
import java.io.PrintWriter;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class FindVirtualCalls extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: FindVirtualCalls.java <输出文件> <十六进制位移> [<十六进制位移> ...]");
            return;
        }
        int n = args.length - 1;
        Pattern[] direct = new Pattern[n];
        Pattern[] load = new Pattern[n];
        for (int i = 0; i < n; i++) {
            String off = args[i + 1].toLowerCase().replaceFirst("^0x", "");
            direct[i] = Pattern.compile("^(CALL|JMP) dword ptr \\[[A-Z]{3} \\+ 0x" + off + "\\]");
            load[i] = Pattern.compile("^MOV ([A-Z]{3}),dword ptr \\[[A-Z]{3} \\+ 0x" + off + "\\]$");
        }
        int hits = 0;
        try (PrintWriter w = new PrintWriter(new File(args[0]), "UTF-8")) {
            w.println("offset\tform\tfunction\tcall_address\tinstruction");
            InstructionIterator it = currentProgram.getListing().getInstructions(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Instruction ins = it.next();
                String s = ins.toString();
                for (int i = 0; i < n; i++) {
                    if (direct[i].matcher(s).find()) {
                        Function f = getFunctionContaining(ins.getAddress());
                        w.println(args[i + 1] + "\tdirect\t" + (f == null ? "-" : f.getEntryPoint().toString())
                                + "\t" + ins.getAddress() + "\t" + s);
                        hits++;
                        continue;
                    }
                    Matcher m = load[i].matcher(s);
                    if (m.find()) {
                        String reg = m.group(1);
                        Instruction nx = ins.getNext();
                        for (int k = 0; k < 8 && nx != null; k++, nx = nx.getNext()) {
                            String t = nx.toString();
                            if (t.equals("CALL " + reg) || t.equals("JMP " + reg)) {
                                Function f = getFunctionContaining(ins.getAddress());
                                w.println(args[i + 1] + "\tloaded\t" + (f == null ? "-" : f.getEntryPoint().toString())
                                        + "\t" + nx.getAddress() + "\t" + s + " ; " + t);
                                hits++;
                                break;
                            }
                            if (t.startsWith("MOV " + reg + ",") || t.startsWith("XOR " + reg + ",") || t.startsWith("POP " + reg)) {
                                break;
                            }
                        }
                    }
                }
            }
        }
        println("命中 " + hits + " 条");
    }
}
