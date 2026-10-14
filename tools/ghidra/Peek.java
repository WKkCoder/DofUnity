// 读取程序镜像里若干地址处的字节(按 Ghidra 的内存视图,已初始化 / 未初始化区分开)。
// 用法(脚本参数):<地址>[:<字节数>] ...,字节数缺省 16。结果经 println 输出(出现在 headless 日志里)。
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript Peek.java 01b08bc0 01a41fc8:8
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;

public class Peek extends GhidraScript {

    @Override
    protected void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        for (String arg : getScriptArgs()) {
            String[] parts = arg.split(":");
            Address addr = toAddr(parts[0]);
            int n = parts.length > 1 ? Integer.parseInt(parts[1]) : 16;
            MemoryBlock block = mem.getBlock(addr);
            Symbol sym = currentProgram.getSymbolTable().getPrimarySymbol(addr);
            StringBuilder sb = new StringBuilder();
            sb.append("PEEK ").append(addr).append(" block=").append(block == null ? "-" : block.getName())
                .append(" initialized=").append(block != null && block.isInitialized())
                .append(" symbol=").append(sym == null ? "-" : sym.getName(true)).append(" bytes=");
            byte[] buf = new byte[n];
            try {
                mem.getBytes(addr, buf);
                for (byte b : buf) {
                    sb.append(String.format("%02x", b & 0xff));
                }
            } catch (Exception e) {
                sb.append("读取失败:").append(e.getMessage());
            }
            println(sb.toString());
        }
    }
}
