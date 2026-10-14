// 列出指向给定地址的所有引用(来源地址、引用类型、所在函数入口与大小)。
// 用法(脚本参数):<地址> ...;结果经 println 输出(出现在 headless 日志里,每行以 XREF 开头)。
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript Xrefs.java 01b08bc0
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class Xrefs extends GhidraScript {

    @Override
    protected void run() throws Exception {
        for (String arg : getScriptArgs()) {
            Address target = toAddr(arg);
            ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(target);
            int n = 0;
            while (it.hasNext()) {
                Reference r = it.next();
                Function f = currentProgram.getFunctionManager().getFunctionContaining(r.getFromAddress());
                println("XREF " + target + " <- " + r.getFromAddress() + " " + r.getReferenceType()
                    + " in " + (f == null ? "-" : f.getEntryPoint() + " size=" + f.getBody().getNumAddresses()));
                n++;
            }
            println("XREF " + target + " total=" + n);
        }
    }
}
