// 把一段地址范围内的反汇编写到文件,并标出每条指令所属的函数入口(没有所属函数的标成 "-")。
// 只读:不改程序,不保存。输出只放在 re/ 下(已被 .gitignore 与提交守卫排除)。
//
// 用法(脚本参数):<输出文件> <起始地址> <结束地址> [最多条数,缺省 4000]
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript AsmRange.java re/notes/range.txt 014e63c0 014e6500
//
// 范围内没有被反汇编的字节,以 "db" 行显示(只给出前 16 个字节,方便判断是不是填充或数据)。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.CodeUnit;
import ghidra.program.model.listing.CodeUnitIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import java.io.File;
import java.io.PrintWriter;

public class AsmRange extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("用法: AsmRange.java <输出文件> <起始地址> <结束地址> [最多条数]");
            return;
        }
        File out = new File(args[0]);
        out.getParentFile().mkdirs();
        Address start = toAddr(args[1]);
        Address end = toAddr(args[2]);
        int limit = args.length > 3 ? Integer.parseInt(args[3]) : 4000;
        Listing listing = currentProgram.getListing();
        String lastOwner = "";
        int n = 0;
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            CodeUnitIterator it = listing.getCodeUnits(new AddressSet(start, end), true);
            while (it.hasNext() && n < limit) {
                CodeUnit u = it.next();
                Function f = listing.getFunctionContaining(u.getAddress());
                String owner = f == null ? "-" : f.getEntryPoint().toString();
                if (!owner.equals(lastOwner)) {
                    w.println("; ---- 函数 " + owner + (f == null ? "" : " 大小 " + f.getBody().getNumAddresses()));
                    lastOwner = owner;
                }
                StringBuilder bytes = new StringBuilder();
                byte[] raw = u.getBytes();
                for (int i = 0; i < raw.length && i < 16; i++) {
                    bytes.append(String.format("%02x", raw[i] & 0xff));
                }
                if (u instanceof Instruction) {
                    w.println(String.format("%s  %-24s %s", u.getAddress(), bytes, u));
                } else {
                    w.println(String.format("%s  %-24s db/data %s", u.getAddress(), bytes, u.getMnemonicString()));
                }
                n++;
            }
        }
        println("已写出 " + n + " 行");
    }
}
