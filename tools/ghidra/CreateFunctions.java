// 在没有函数的地址上反汇编并建立函数(只改分析工程副本,例如 dnffix,不带 -readOnly 运行)。
// 用途:原生接口的实现函数只通过函数指针常量被引用(MOV EAX,0x5b7060),没有直接调用者,
// Ghidra 的自动分析不会把它们当函数,DumpFunction 就取不到。
//
// 用法(脚本参数):<报告文件> <地址 1> [<地址 2> ...]
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis \
//       -scriptPath tools/ghidra -postScript CreateFunctions.java re/notes/create_functions.tsv 005b7060 ...
//
// 报告每个地址的结果:exists(已有函数)/ created(新建,带大小)/ inside(落在别的函数体内,不动)/ failed。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.File;
import java.io.PrintWriter;

public class CreateFunctions extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: CreateFunctions.java <报告文件> <地址> [<地址> ...]");
            return;
        }
        int created = 0;
        try (PrintWriter w = new PrintWriter(new File(args[0]), "UTF-8")) {
            w.println("address\tresult\tsize");
            for (int i = 1; i < args.length; i++) {
                Address a = toAddr(args[i]);
                Function f = getFunctionAt(a);
                if (f != null) {
                    w.println(args[i] + "\texists\t" + f.getBody().getNumAddresses());
                    continue;
                }
                Function containing = getFunctionContaining(a);
                if (containing != null) {
                    w.println(args[i] + "\tinside " + containing.getEntryPoint() + "\t" + containing.getBody().getNumAddresses());
                    continue;
                }
                if (getInstructionAt(a) == null) {
                    disassemble(a);
                }
                Function nf = createFunction(a, null);
                if (nf == null) {
                    w.println(args[i] + "\tfailed\t0");
                } else {
                    created++;
                    w.println(args[i] + "\tcreated\t" + nf.getBody().getNumAddresses());
                }
            }
        }
        println("新建函数 " + created + " 个");
    }
}
