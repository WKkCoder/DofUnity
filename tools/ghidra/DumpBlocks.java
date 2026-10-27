// 把一个超大函数里的若干"处理块"各自当作临时函数反编译,写到文件,供分析者本地阅读。
// 用途:状态进入槽、每帧槽这类几十 KB 的巨型分派函数整体反编译会报"Response buffer size exceeded"或要几十分钟,
// 而每个状态的处理块只有一两 KB,临时把它当函数就能读成 C。块用到的寄存器(this、帧指针)在反编译里显示成 unaff_ 变量。
//
// 临时函数只存在于内存里:请用 -readOnly 运行,工程不会被改动。输出只放在 re/(已被 .gitignore 与提交守卫排除),绝不提交;
// 进台账的只有用自己的话写的结论与地址(不含反编译出的原版代码)。
//
// 用法(脚本参数):<输出目录> <块起始地址>[-<块结束地址>] [...]
//   analyzeHeadless re/ghidra/projects dnffix -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript DumpBlocks.java re/scratch/m38/blocks 00cd476e 00cd4bb6-00cd4e97
// 给了结束地址就把 [起始, 结束) 之外的指令从函数体里去掉(块末尾直接跳到公共出口时,Ghidra 会把出口也算进函数体,这样更干净);
// 没给结束地址就让 Ghidra 按控制流自己算函数体。
// 每个块产出:<起始地址>.blk.asm.txt(反汇编)与 <起始地址>.blk.c.txt(反编译)。
//
// 环境变量:DNF_DECOMPILE_TIMEOUT(秒,默认 120)、DNF_DECOMPILE_PAYLOAD_MB(最大载荷,默认 Ghidra 的设置)。
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.address.AddressSetView;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import java.io.File;
import java.io.PrintWriter;

public class DumpBlocks extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("用法: DumpBlocks.java <输出目录> <块起始地址>[-<块结束地址>] ...");
            return;
        }
        File outDir = new File(args[0]);
        outDir.mkdirs();

        int decompileTimeout = 120;
        String timeoutText = System.getenv("DNF_DECOMPILE_TIMEOUT");
        if (timeoutText != null && !timeoutText.isEmpty()) {
            decompileTimeout = Integer.parseInt(timeoutText.trim());
        }
        DecompInterface decomp = new DecompInterface();
        String payloadText = System.getenv("DNF_DECOMPILE_PAYLOAD_MB");
        if (payloadText != null && !payloadText.isEmpty()) {
            DecompileOptions options = new DecompileOptions();
            options.setMaxPayloadMBytes(Integer.parseInt(payloadText.trim()));
            decomp.setOptions(options);
        }
        decomp.openProgram(currentProgram);
        try {
            for (int i = 1; i < args.length; i++) {
                String spec = args[i];
                String startText = spec;
                String endText = null;
                int dash = spec.indexOf('-');
                if (dash > 0) {
                    startText = spec.substring(0, dash);
                    endText = spec.substring(dash + 1);
                }
                Address start = toAddr(startText);
                Address end = endText == null ? null : toAddr(endText);
                dumpBlock(decomp, outDir, start, end, decompileTimeout);
            }
        } finally {
            decomp.dispose();
        }
    }

    private void dumpBlock(DecompInterface decomp, File outDir, Address start, Address end, int timeout) throws Exception {
        String stem = String.format("%08x", start.getOffset());
        // 起始地址落在已有函数里(巨型函数):先把那个函数从内存里的工程去掉,块才能成为独立函数
        Function parent = getFunctionContaining(start);
        if (parent != null) {
            removeFunction(parent);
        }
        Function f = createFunction(start, "blk_" + stem);
        if (f == null) {
            printerr("建不出临时函数: " + stem);
            return;
        }
        if (end != null) {
            // 函数体限定在 [start, end) 内;超出的指令不参与反编译
            AddressSet body = new AddressSet(start, end.subtract(1));
            AddressSetView old = f.getBody();
            AddressSet keep = old.intersect(body);
            f.setBody(keep);
            // Ghidra 的函数体必须包含入口;intersect 之后入口仍在
        }
        try (PrintWriter asm = new PrintWriter(new File(outDir, stem + ".blk.asm.txt"), "UTF-8")) {
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                StringBuilder bytes = new StringBuilder();
                for (byte b : ins.getBytes()) {
                    bytes.append(String.format("%02x", b & 0xff));
                }
                asm.println(String.format("%08x  %-20s %s", ins.getAddress().getOffset(), bytes, ins.toString()));
            }
        }
        DecompileResults res = decomp.decompileFunction(f, timeout, monitor);
        try (PrintWriter c = new PrintWriter(new File(outDir, stem + ".blk.c.txt"), "UTF-8")) {
            if (res != null && res.decompileCompleted() && res.getDecompiledFunction() != null) {
                c.println(res.getDecompiledFunction().getC());
            } else {
                c.println("// 反编译失败: " + (res == null ? "无结果" : res.getErrorMessage()));
            }
        }
        println("已写出 " + stem);
    }
}
