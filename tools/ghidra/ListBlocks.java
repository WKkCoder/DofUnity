// 列出程序的内存块(名称、起止地址、是否有初始化内容、权限)。只读,不改程序。
//
// 用法:analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//          -scriptPath tools/ghidra -postScript ListBlocks.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.MemoryBlock;

public class ListBlocks extends GhidraScript {

    @Override
    protected void run() throws Exception {
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            println(String.format("块 %-12s %s - %s  大小 0x%x  初始化=%s  权限=%s%s%s",
                b.getName(), b.getStart(), b.getEnd(), b.getSize(), b.isInitialized(),
                b.isRead() ? "r" : "-", b.isWrite() ? "w" : "-", b.isExecute() ? "x" : "-"));
        }
    }
}
