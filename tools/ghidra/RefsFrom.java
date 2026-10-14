// 列出给定地址处指令的所有"从这里出发"的引用(类型与目标),以及这条指令所在的函数。
// 只读:不改程序,不保存。输出走 println(出现在 headless 日志里)。
//
// 用法(脚本参数):<地址> [<地址> ...]
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript RefsFrom.java 00be34eb
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.symbol.Reference;

public class RefsFrom extends GhidraScript {

    @Override
    protected void run() throws Exception {
        for (String arg : getScriptArgs()) {
            Address a = toAddr(arg);
            Instruction ins = currentProgram.getListing().getInstructionAt(a);
            Function f = currentProgram.getListing().getFunctionContaining(a);
            println("REFS " + a + " insn=" + (ins == null ? "-" : ins.toString())
                + " function=" + (f == null ? "-" : f.getEntryPoint() + " body=" + f.getBody().getNumAddresses()
                + " ranges=" + f.getBody().getNumAddressRanges()));
            if (ins == null) {
                continue;
            }
            Reference[] refs = ins.getReferencesFrom();
            for (int i = 0; i < refs.length && i < 12; i++) {
                println("  -> " + refs[i].getToAddress() + " " + refs[i].getReferenceType() + " source=" + refs[i].getSource());
            }
            println("  refs=" + refs.length);
        }
    }
}
