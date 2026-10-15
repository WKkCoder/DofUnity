// 列出某个地址范围内、目标落进"受保护范围"(PE 节 #3 至 #8,VA 0x01dd9000 至 0x02c98000)的跳转与调用。
// 只看"这条指令跳去哪里"这一个数字,不读目标处的任何字节,不分析受保护区域。
// 用途:明文函数里夹着几处"洞"(跳进受保护区域的跳板),要知道有几处、各在哪、跳回哪里,
//       才能判断这个函数还有多少部分是可读的。
//
// 用法(脚本参数):<起始地址> <结束地址(不含)>;每行以 HOLE 开头:
//   HOLE <指令地址> <助记符> <目标地址> <所在函数入口>
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript ProtectedJumpsIn.java 00e5fc6d 00e67d00
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.symbol.FlowType;

public class ProtectedJumpsIn extends GhidraScript {

    private static final long PROTECTED_START = 0x01dd9000L;
    private static final long PROTECTED_END = 0x02c98000L;

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: ProtectedJumpsIn.java <起始地址> <结束地址>");
            return;
        }
        Address lo = toAddr(args[0]);
        Address hi = toAddr(args[1]);
        InstructionIterator it = currentProgram.getListing().getInstructions(new AddressSet(lo, hi), true);
        int holes = 0;
        int total = 0;
        while (it.hasNext() && !monitor.isCancelled()) {
            Instruction ins = it.next();
            total++;
            FlowType ft = ins.getFlowType();
            if (!(ft.isJump() || ft.isCall())) {
                continue;
            }
            for (Address t : ins.getFlows()) {
                long o = t.getOffset();
                if (o >= PROTECTED_START && o < PROTECTED_END) {
                    Function f = getFunctionContaining(ins.getAddress());
                    println("HOLE " + ins.getAddress() + " " + ins.getMnemonicString() + " " + t + " "
                            + (f == null ? "-" : f.getEntryPoint().toString()));
                    holes++;
                }
            }
        }
        println("HOLE total=" + holes + " 指令数=" + total);
    }
}
