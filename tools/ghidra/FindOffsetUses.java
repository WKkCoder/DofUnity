// 找出所有带某个位移的内存操作数的指令(例如 [ESI + 0x670]),按所在函数汇总。
// 用途:C++ 对象的字段没有名字,只有偏移;要知道"谁读写了这个字段",就按偏移查一遍,再人工用上下文排除别的类里偶然相同的偏移。
//
// 用法(脚本参数):<输出文件> <十六进制偏移> [<十六进制偏移> ...]
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript FindOffsetUses.java re/notes/uses_670.tsv 670
//
// 输出 TSV:offset, function, address, instruction。只匹配文本形如 "[REG + 0x670]" 或 "[REG + REG*N + 0x670]" 的操作数。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.io.File;
import java.io.PrintWriter;
import java.util.regex.Pattern;

public class FindOffsetUses extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: FindOffsetUses.java <输出文件> <十六进制偏移> [<十六进制偏移> ...]");
            return;
        }
        int n = args.length - 1;
        Pattern[] pats = new Pattern[n];
        for (int i = 0; i < n; i++) {
            String off = args[i + 1].toLowerCase().replaceFirst("^0x", "");
            pats[i] = Pattern.compile("\\+ 0x" + off + "\\]");
        }
        int hits = 0;
        try (PrintWriter w = new PrintWriter(new File(args[0]), "UTF-8")) {
            w.println("offset\tfunction\taddress\tinstruction");
            InstructionIterator it = currentProgram.getListing().getInstructions(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Instruction ins = it.next();
                String s = ins.toString();
                if (s.indexOf('[') < 0) {
                    continue;
                }
                for (int i = 0; i < n; i++) {
                    if (pats[i].matcher(s).find()) {
                        Function f = getFunctionContaining(ins.getAddress());
                        w.println(args[i + 1] + "\t" + (f == null ? "-" : f.getEntryPoint().toString()) + "\t" + ins.getAddress() + "\t" + s);
                        hits++;
                    }
                }
            }
        }
        println("命中 " + hits + " 条");
    }
}
