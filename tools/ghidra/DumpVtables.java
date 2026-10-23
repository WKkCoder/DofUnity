// 按符号名列出虚表(带符号的程序,例如服务端二进制):每个命中的符号打印地址,再把后面若干个 4 字节槽读出来,
// 能解析成函数的写出函数名。只读。
// 用法(脚本参数):<输出文件> <槽数> <符号名完整匹配 1> [<符号名完整匹配 2> ...]
//   analyzeHeadless re/ghidra/projects dfgame -process df_game_r.orig -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript DumpVtables.java re/notes/server/vt.txt 40 _ZTV10CEquipItem _ZTV5CItem
// 输出放 re/(不入库)。符号名要完整(不是片段);虚表前两个槽是 offset-to-top 与 typeinfo,所以第一个函数在偏移 8。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import java.io.File;
import java.io.PrintWriter;

public class DumpVtables extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("用法: DumpVtables.java <输出文件> <槽数> <符号名> [<符号名> ...]");
            return;
        }
        File out = new File(args[0]);
        if (out.getParentFile() != null) {
            out.getParentFile().mkdirs();
        }
        int slots = Integer.parseInt(args[1]);
        int n = 0;
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            for (int a = 2; a < args.length; a++) {
                SymbolIterator it = currentProgram.getSymbolTable().getSymbols(args[a]);
                boolean any = false;
                while (it.hasNext()) {
                    Symbol s = it.next();
                    any = true;
                    Address base = s.getAddress();
                    w.println("== " + args[a] + " @ " + base);
                    for (int i = 0; i < slots; i++) {
                        Address at = base.add(i * 4L);
                        long raw;
                        try {
                            raw = currentProgram.getMemory().getInt(at) & 0xffffffffL;
                        } catch (Exception e) {
                            break;
                        }
                        String name = "";
                        try {
                            Address target = toAddr(raw);
                            Function f = getFunctionAt(target);
                            if (f != null) {
                                name = f.getName(true);
                            } else {
                                name = cString(target);
                            }
                        } catch (Exception e) {
                            // 不是地址
                        }
                        w.println(String.format("  +0x%02x  %08x  %s", i * 4, raw, name));
                    }
                    n++;
                }
                if (!any) {
                    w.println("== " + args[a] + " 没有找到");
                }
            }
        }
        println("VT 命中 " + n + " 个虚表 -> " + out.getPath());
    }

    /** 目标地址上若是一段可打印的 ASCII 串(不超过 48 个字符),返回它加引号;否则返回空串。用于读"字符串指针数组"。 */
    private String cString(Address target) {
        StringBuilder sb = new StringBuilder();
        try {
            for (int i = 0; i < 48; i++) {
                int b = currentProgram.getMemory().getByte(target.add(i)) & 0xff;
                if (b == 0) {
                    return i == 0 ? "" : "\"" + sb + "\"";
                }
                if (b < 0x20 || b > 0x7e) {
                    return "";
                }
                sb.append((char) b);
            }
        } catch (Exception e) {
            return "";
        }
        return "";
    }
}
