// 按名字片段列出函数(带符号的程序,例如服务端二进制):地址、大小、完整名字。只读。
// 用法(脚本参数):<输出文件> <名字片段 1> [<名字片段 2> ...](不区分大小写,任一片段命中即输出)
//   analyzeHeadless re/ghidra/projects dfgame -process df_game_r.orig -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript FindFunctionsByName.java re/notes/server/fn_attackinfo.tsv AttackInfo
// 输出放 re/(不入库)。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

public class FindFunctionsByName extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: FindFunctionsByName.java <输出文件> <名字片段> [<名字片段> ...]");
            return;
        }
        File out = new File(args[0]);
        if (out.getParentFile() != null) {
            out.getParentFile().mkdirs();
        }
        List<String> needles = new ArrayList<>();
        for (int i = 1; i < args.length; i++) {
            needles.add(args[i].toLowerCase());
        }
        int n = 0;
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            w.println("address\tsize\tname");
            FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
            while (it.hasNext()) {
                Function f = it.next();
                String name = f.getName(true);
                String low = name.toLowerCase();
                boolean hit = false;
                for (String s : needles) {
                    if (low.contains(s)) {
                        hit = true;
                        break;
                    }
                }
                if (hit) {
                    w.println(f.getEntryPoint() + "\t" + f.getBody().getNumAddresses() + "\t" + name);
                    n++;
                }
            }
        }
        println("FIND 命中 " + n + " 个函数 -> " + out.getPath());
    }
}
