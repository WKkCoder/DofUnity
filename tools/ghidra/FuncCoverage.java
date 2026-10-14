// 统计"函数覆盖":可执行内存块里,有多少字节在 Ghidra 已建立的函数里,有多少落在函数之间的空隙里。
// 只读:不改程序,不保存。输出只放在 re/ 下。
//
// 背景(E-041 之后):字符串访问函数的调用点里,有近一万个落在"没有所属函数"的地址,
// 其中一大片集中在 0x14E63D5 之后。这里要弄清楚这些地址是没被识别成函数的代码,还是别的东西。
//
// 用法(脚本参数):<输出文件> [最小空隙字节数,缺省 4096]
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript FuncCoverage.java re/notes/func_coverage.tsv
//
// 输出每行(制表符分隔):起始地址 | 结束地址 | 字节数 | 其中已反汇编成指令的字节数 | 其中被定义成数据的字节数 | 所在内存块 | 前一个函数入口 | 后一个函数入口
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressRange;
import ghidra.program.model.address.AddressRangeIterator;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.address.AddressSetView;
import ghidra.program.model.listing.CodeUnit;
import ghidra.program.model.listing.CodeUnitIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.mem.MemoryBlock;
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

public class FuncCoverage extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("用法: FuncCoverage.java <输出文件> [最小空隙字节数]");
            return;
        }
        long minGap = args.length > 1 ? Long.parseLong(args[1]) : 4096;
        File out = new File(args[0]);
        out.getParentFile().mkdirs();
        Listing listing = currentProgram.getListing();

        AddressSet exec = new AddressSet();
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            if (b.isExecute() && b.isInitialized()) {
                exec.add(b.getStart(), b.getEnd());
            }
        }
        AddressSet funcs = new AddressSet();
        int nFuncs = 0;
        FunctionIterator fit = listing.getFunctions(true);
        while (fit.hasNext()) {
            Function f = fit.next();
            funcs.add(f.getBody());
            nFuncs++;
        }
        AddressSet gaps = exec.subtract(funcs);
        long execBytes = exec.getNumAddresses();
        long funcBytes = exec.intersect(funcs).getNumAddresses();
        long gapBytes = gaps.getNumAddresses();
        println("COVERAGE exec_bytes=" + execBytes + " in_functions=" + funcBytes + " gaps=" + gapBytes
            + " functions=" + nFuncs);

        List<long[]> dummy = new ArrayList<>();
        long gapInsn = 0;
        long gapData = 0;
        try (PrintWriter pw = new PrintWriter(out, "UTF-8")) {
            AddressRangeIterator it = gaps.getAddressRanges();
            while (it.hasNext()) {
                AddressRange r = it.next();
                long size = r.getLength();
                long insnBytes = 0;
                long dataBytes = 0;
                CodeUnitIterator cu = listing.getCodeUnits(new AddressSet(r.getMinAddress(), r.getMaxAddress()), true);
                while (cu.hasNext()) {
                    CodeUnit u = cu.next();
                    if (u instanceof ghidra.program.model.listing.Instruction) {
                        insnBytes += u.getLength();
                    } else if (u instanceof ghidra.program.model.listing.Data) {
                        ghidra.program.model.listing.Data d = (ghidra.program.model.listing.Data) u;
                        if (d.isDefined()) {
                            dataBytes += u.getLength();
                        }
                    }
                }
                gapInsn += insnBytes;
                gapData += dataBytes;
                if (size < minGap) {
                    continue;
                }
                MemoryBlock blk = currentProgram.getMemory().getBlock(r.getMinAddress());
                Function prev = listing.getFunctionContaining(r.getMinAddress().subtract(1));
                Function next = getFunctionAfter(r.getMaxAddress());
                pw.println(r.getMinAddress() + "\t" + r.getMaxAddress() + "\t" + size + "\t" + insnBytes + "\t"
                    + dataBytes + "\t" + (blk == null ? "-" : blk.getName()) + "\t"
                    + (prev == null ? "-" : prev.getEntryPoint().toString()) + "\t"
                    + (next == null ? "-" : next.getEntryPoint().toString()));
            }
        }
        println("GAPS instruction_bytes=" + gapInsn + " defined_data_bytes=" + gapData);
    }
}
