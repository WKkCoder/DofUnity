// 在给定范围里挑出"有引用指向、但没有被反汇编成指令"的地址,打印每个地址的引用类型与前 32 个字节。
// 用来看清这些字节像不像正常的 x86 代码(判断是没分析到,还是被加密/混淆过)。
// 只读:不改程序,不保存。输出走 println(出现在 headless 日志里)。
//
// 用法(脚本参数):<起始地址-结束地址> <个数> [<步长,缺省 1>]
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript SampleUndefinedRefs.java 0216a000-0297c000 8 400
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressIterator;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class SampleUndefinedRefs extends GhidraScript {

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        String[] p = args[0].split("-");
        AddressSet set = new AddressSet(toAddr(p[0]), toAddr(p[1]));
        int want = Integer.parseInt(args[1]);
        int step = args.length > 2 ? Integer.parseInt(args[2]) : 1;
        int seen = 0;
        int shown = 0;
        long undefinedReferenced = 0;
        AddressIterator it = currentProgram.getReferenceManager().getReferenceDestinationIterator(set, true);
        while (it.hasNext() && shown < want) {
            Address a = it.next();
            if (currentProgram.getListing().getInstructionAt(a) != null
                || currentProgram.getListing().getFunctionContaining(a) != null) {
                continue;
            }
            undefinedReferenced++;
            if ((seen++ % step) != 0) {
                continue;
            }
            ReferenceIterator rit = currentProgram.getReferenceManager().getReferencesTo(a);
            String kinds = "";
            int n = 0;
            while (rit.hasNext() && n < 3) {
                Reference r = rit.next();
                kinds += r.getReferenceType() + "<-" + r.getFromAddress() + " ";
                n++;
            }
            byte[] buf = new byte[32];
            StringBuilder sb = new StringBuilder();
            try {
                currentProgram.getMemory().getBytes(a, buf);
                for (byte b : buf) {
                    sb.append(String.format("%02x", b & 0xff));
                }
            } catch (Exception e) {
                sb.append("读取失败");
            }
            println("SAMPLE " + a + " refs[" + kinds.trim() + "] bytes=" + sb);
            shown++;
        }
        println("SAMPLED " + args[0] + " shown=" + shown + " undefined_referenced_seen=" + undefinedReferenced);
    }
}
