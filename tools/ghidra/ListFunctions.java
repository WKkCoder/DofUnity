// 列出地址范围内所有函数的入口与大小(只读)。
// 用法(脚本参数):<起始地址> <结束地址(不含)>;每行以 FUNC 开头:FUNC <入口> <大小> <是否在受保护范围(0/1)>
//   analyzeHeadless re/ghidra/projects dnfparam2 -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript ListFunctions.java 008d0000 008e8000
// 受保护范围(PE 节 #3 至 #8,VA 0x01dd9000 起)里的函数只列出、不应被分析;这里只做标记。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;

public class ListFunctions extends GhidraScript {

    private static final long PROTECTED_START = 0x01dd9000L;

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("用法: ListFunctions.java <起始地址> <结束地址>");
            return;
        }
        Address start = toAddr(args[0]);
        Address end = toAddr(args[1]);
        AddressSet range = new AddressSet(start, end.subtract(1));
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(range, true);
        int n = 0;
        while (it.hasNext()) {
            Function f = it.next();
            long entry = f.getEntryPoint().getOffset();
            println("FUNC " + f.getEntryPoint() + " " + f.getBody().getNumAddresses() + " " + (entry >= PROTECTED_START ? 1 : 0));
            n++;
        }
        println("FUNC total=" + n);
    }
}
