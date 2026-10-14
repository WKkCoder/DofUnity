// 只读静态调查(P6)。在已分析过的 DNF.exe 工程上运行,不修改工程,不输出任何反汇编或反编译文本。
//
// 输出(TSV,写到第一个脚本参数给的目录,默认 re/ghidra/out):
//   blocks.tsv        每个内存块:权限、已反汇编的字节数、已定义数据字节数、函数个数、字符串个数
//   ror6.tsv          所有 "ror x, 6" 指令:地址、所在函数入口、函数大小(P1 里 PVF 解密用的是 ROR 6)
//   refs_const.tsv    对 PVF 解密常量所在地址附近的引用:谁、怎么引用、在哪个函数里
//   const_context.hex 该地址前后各 64 字节的原始字节(只放在 re/ 里,不提交)
//   summary.txt       总数
//
// 运行:
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript P6Survey.java re/ghidra/out
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

public class P6Survey extends GhidraScript {

    // 来自 tools/py/pe_probe.py:0x81A79011 在文件里只出现一次,位于这个虚拟地址
    private static final long CONST_VA = 0x01A41FC8L;

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        File outDir = new File(args.length > 0 ? args[0] : "re/ghidra/out");
        outDir.mkdirs();

        Listing listing = currentProgram.getListing();
        FunctionManager fm = currentProgram.getFunctionManager();

        long totalInstr = 0;
        long totalCode = 0;
        long totalFuncs = 0;
        long totalBytes = 0;

        // ---------------------------------------------------------------- blocks.tsv
        try (PrintWriter w = new PrintWriter(new File(outDir, "blocks.tsv"), "UTF-8")) {
            w.println("block\tstart\tend\tsize\tperm\tinitialized\tcode_bytes\tcode_pct\tinstructions\tdata_bytes\tfunctions\tstrings");
            for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
                AddressSet set = new AddressSet(b.getStart(), b.getEnd());
                long codeBytes = 0;
                long instrCount = 0;
                for (Instruction ins : listing.getInstructions(set, true)) {
                    codeBytes += ins.getLength();
                    instrCount++;
                }
                long dataBytes = 0;
                long strings = 0;
                for (Data d : listing.getDefinedData(set, true)) {
                    dataBytes += d.getLength();
                    String t = d.getDataType().getName().toLowerCase();
                    if (t.contains("string") || t.contains("unicode")) {
                        strings++;
                    }
                }
                long funcs = 0;
                for (Function f : fm.getFunctions(set, true)) {
                    funcs++;
                }
                String perm = (b.isRead() ? "R" : "-") + (b.isWrite() ? "W" : "-") + (b.isExecute() ? "X" : "-");
                long size = b.getSize();
                w.println(b.getName() + "\t" + b.getStart() + "\t" + b.getEnd() + "\t" + size + "\t" + perm + "\t"
                        + b.isInitialized() + "\t" + codeBytes + "\t"
                        + String.format("%.2f", 100.0 * codeBytes / Math.max(1, size)) + "\t" + instrCount + "\t"
                        + dataBytes + "\t" + funcs + "\t" + strings);
                totalInstr += instrCount;
                totalCode += codeBytes;
                totalFuncs += funcs;
                totalBytes += size;
            }
        }

        // ---------------------------------------------------------------- ror6.tsv
        List<String> rorRows = new ArrayList<>();
        InstructionIterator it = listing.getInstructions(true);
        long ror6 = 0;
        while (it.hasNext()) {
            Instruction ins = it.next();
            if (!"ROR".equalsIgnoreCase(ins.getMnemonicString()) || ins.getNumOperands() < 2) {
                continue;
            }
            Scalar sc = ins.getScalar(1);
            if (sc == null || sc.getUnsignedValue() != 6) {
                continue;
            }
            ror6++;
            Function f = fm.getFunctionContaining(ins.getAddress());
            String entry = f == null ? "-" : f.getEntryPoint().toString();
            long fsize = f == null ? 0 : f.getBody().getNumAddresses();
            rorRows.add(ins.getAddress() + "\t" + entry + "\t" + fsize);
        }
        try (PrintWriter w = new PrintWriter(new File(outDir, "ror6.tsv"), "UTF-8")) {
            w.println("instruction\tfunction_entry\tfunction_size");
            for (String r : rorRows) {
                w.println(r);
            }
        }

        // ---------------------------------------------------------------- refs_const.tsv
        long refCount = 0;
        try (PrintWriter w = new PrintWriter(new File(outDir, "refs_const.tsv"), "UTF-8")) {
            w.println("target\tfrom\ttype\tfunction_entry\tfunction_size");
            for (long delta = -16; delta <= 24; delta++) {
                Address target = toAddr(CONST_VA + delta);
                for (Reference r : getReferencesTo(target)) {
                    Function f = fm.getFunctionContaining(r.getFromAddress());
                    String entry = f == null ? "-" : f.getEntryPoint().toString();
                    long fsize = f == null ? 0 : f.getBody().getNumAddresses();
                    w.println(target + "\t" + r.getFromAddress() + "\t" + r.getReferenceType() + "\t" + entry + "\t" + fsize);
                    refCount++;
                }
            }
        }

        // ---------------------------------------------------------------- const_context.hex
        try (PrintWriter w = new PrintWriter(new File(outDir, "const_context.hex"), "UTF-8")) {
            Address start = toAddr(CONST_VA - 64);
            byte[] buf = new byte[128 + 4];
            int n = currentProgram.getMemory().getBytes(start, buf);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < n; i++) {
                if (i % 16 == 0) {
                    sb.append(String.format("%n%s: ", start.add(i)));
                }
                sb.append(String.format("%02x ", buf[i] & 0xff));
            }
            w.println(sb.toString().trim());
        }

        // ---------------------------------------------------------------- summary.txt
        try (PrintWriter w = new PrintWriter(new File(outDir, "summary.txt"), "UTF-8")) {
            w.println("image_base\t" + currentProgram.getImageBase());
            w.println("language\t" + currentProgram.getLanguageID());
            w.println("memory_bytes\t" + totalBytes);
            w.println("instructions\t" + totalInstr);
            w.println("code_bytes\t" + totalCode);
            w.println("functions_in_blocks\t" + totalFuncs);
            w.println("functions_total\t" + fm.getFunctionCount());
            w.println("ror6_instructions\t" + ror6);
            w.println("refs_to_const_area\t" + refCount);
        }
        println("P6Survey 完成,输出在 " + outDir.getAbsolutePath());
    }
}
