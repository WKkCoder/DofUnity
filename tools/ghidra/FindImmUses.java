// 找出"指令里直接带某个 32 位常量/地址"的地方,按函数列出。
// 典型用途:
//   - 知道一个虚表地址(例如 0x01705fac),想找写入它的构造函数(MOV [REG],0x1705fac);
//   - 知道一个全局变量或常量表的地址,想找谁在用它。
// 匹配方式是对每条指令的文本找 "0x<十六进制>"(不区分前导零,要求前后不是十六进制字符),
// 所以 "MOV dword ptr [ESI],0x1705fac"、"PUSH 0x1705fac"、"MOV EAX,[0x01705fac]" 都会命中。
// 只读静态,不运行程序。
//
// 用法(脚本参数):<输出文件> <十六进制值> [<十六进制值> ...]
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript FindImmUses.java out/hero/imm_uses.tsv 1705fac 1705544
//
// 输出 TSV:value, function, address, instruction。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.io.File;
import java.io.PrintWriter;
import java.util.regex.Pattern;

public class FindImmUses extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: FindImmUses.java <输出文件> <十六进制值> [<十六进制值> ...]");
            return;
        }
        int n = args.length - 1;
        Pattern[] pats = new Pattern[n];
        for (int i = 0; i < n; i++) {
            String v = args[i + 1].toLowerCase().replaceFirst("^0x", "").replaceFirst("^0+(?=.)", "");
            pats[i] = Pattern.compile("0x0*" + v + "(?![0-9a-f])");
        }
        int hits = 0;
        try (PrintWriter w = new PrintWriter(new File(args[0]), "UTF-8")) {
            w.println("value\tfunction\taddress\tinstruction");
            InstructionIterator it = currentProgram.getListing().getInstructions(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Instruction ins = it.next();
                String s = ins.toString();
                if (s.indexOf("0x") < 0) {
                    continue;
                }
                for (int i = 0; i < n; i++) {
                    if (pats[i].matcher(s).find()) {
                        Function f = getFunctionContaining(ins.getAddress());
                        w.println(args[i + 1] + "\t" + (f == null ? "-" : f.getEntryPoint().toString())
                                + "\t" + ins.getAddress() + "\t" + s);
                        hits++;
                    }
                }
            }
        }
        println("命中 " + hits + " 条");
    }
}
