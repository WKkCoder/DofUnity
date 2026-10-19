// 列出一段地址范围内的所有函数:入口、大小、被引用次数(调用与数据引用都算)。
// 用途:C++ 类的方法在内存里是挨在一起的(同一个源文件的函数常常连续排放),知道一个方法的地址后,
// 看它前后有哪些函数,能很快圈出同一个类的其余方法(例如"移动组件"的设速度、设加速度、每帧推进)。
// 只读:不改程序,不保存。输出只放在 re/ 下(已被 .gitignore 与提交守卫排除)。
// 与 ListFunctions.java 不同:那个只往标准输出列 FUNC 行并标记受保护范围,这个写 TSV 文件并带引用计数。
//
// 用法(脚本参数):<输出文件> <起始地址> <结束地址>
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript ListFunctionRefs.java re/notes/funcs.tsv 008b0000 008b3000
//
// 输出 TSV:entry、size、refs。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import java.io.File;
import java.io.PrintWriter;

public class ListFunctionRefs extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("用法: ListFunctionRefs.java <输出文件> <起始地址> <结束地址>");
            return;
        }
        Address lo = toAddr(Long.parseLong(args[1], 16));
        Address hi = toAddr(Long.parseLong(args[2], 16));
        int n = 0;
        try (PrintWriter w = new PrintWriter(new File(args[0]), "UTF-8")) {
            w.println("entry\tsize\trefs");
            FunctionIterator it = currentProgram.getFunctionManager().getFunctions(lo, true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Function f = it.next();
                Address e = f.getEntryPoint();
                if (e.compareTo(hi) >= 0) {
                    break;
                }
                int refs = currentProgram.getReferenceManager().getReferenceCountTo(e);
                w.println(String.format("%08x\t%d\t%d", e.getOffset(), f.getBody().getNumAddresses(), refs));
                n++;
            }
        }
        println("ListFunctionRefs 写出 " + n + " 个函数 -> " + args[0]);
    }
}
