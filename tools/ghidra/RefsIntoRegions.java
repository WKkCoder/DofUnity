// 统计"程序里已有的引用"落进某些地址范围的情况:引用类型分布、被引用的不同地址数、来源函数数。
// 用来判断一段没被反汇编的字节更像代码(有调用与跳转引用)还是数据(只有读写引用)。
// 只读:不改程序,不保存。输出走 println(出现在 headless 日志里)。
//
// 用法(脚本参数):<起始地址-结束地址> [<起始地址-结束地址> ...]
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript RefsIntoRegions.java 019d9000-01cef000 01d6a000-0257c000
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressIterator;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import java.util.HashSet;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

public class RefsIntoRegions extends GhidraScript {

    @Override
    protected void run() throws Exception {
        for (String arg : getScriptArgs()) {
            String[] p = arg.split("-");
            Address lo = toAddr(p[0]);
            Address hi = toAddr(p[1]);
            AddressSet set = new AddressSet(lo, hi);
            Map<String, Integer> byType = new TreeMap<>();
            Set<Address> dests = new HashSet<>();
            Set<Address> srcFuncs = new HashSet<>();
            long total = 0;
            AddressIterator it = currentProgram.getReferenceManager().getReferenceDestinationIterator(set, true);
            while (it.hasNext()) {
                Address dest = it.next();
                dests.add(dest);
                ReferenceIterator rit = currentProgram.getReferenceManager().getReferencesTo(dest);
                while (rit.hasNext()) {
                    Reference r = rit.next();
                    byType.merge(r.getReferenceType().toString(), 1, Integer::sum);
                    total++;
                    Function f = currentProgram.getListing().getFunctionContaining(r.getFromAddress());
                    if (f != null) {
                        srcFuncs.add(f.getEntryPoint());
                    }
                }
            }
            println("REGION " + arg + " size=" + set.getNumAddresses() + " referenced_addresses=" + dests.size()
                + " references=" + total + " source_functions=" + srcFuncs.size() + " by_type=" + byType);
        }
    }
}
