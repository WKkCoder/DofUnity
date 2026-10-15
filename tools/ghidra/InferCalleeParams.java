// 为"目标函数的直接被调用者"推断参数,让之后的反编译结果能显示调用参数(否则 -noanalysis 的工程里
// 被调用者没有原型,调用处的参数全被丢掉,字符串字面量地址看不见)。
//
// 做法(与 Ghidra 自带的 Decompiler Parameter ID 分析器同类,但只处理直接被调用者,不递归):
//   1. 找出目标函数里所有直接 CALL 的目标;
//   2. 受保护区域(默认 0x01dd9000-0x02c98000)里的函数,以及带有"跳进该区域"指令的函数(跳板、尾跳)一律跳过:
//      不对它们做任何分析,也不让反编译器顺着跳转读进那里的字节;
//   3. 对其余被调用者先用普通模式反编译,若局部符号里有对 ECX 的未定义输入(in_ECX),就把调用约定设为
//      __thiscall;再用 paramid 模式反编译并把参数与返回值写进工程。
// 只应对工程的"副本"运行(会改工程);原始的 dnffix 工程不动。
//
// 用法(脚本参数):<目标函数地址> [<目标函数地址> ...]
//   环境变量:DNF_PROTECTED_LO、DNF_PROTECTED_HI(十六进制,默认见上)
//   analyzeHeadless re/ghidra/projects dnfparam -process DNF.exe -noanalysis \
//       -scriptPath tools/ghidra -postScript InferCalleeParams.java 01333530
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.InstructionIterator;
import ghidra.program.model.pcode.HighFunction;
import ghidra.program.model.pcode.HighFunctionDBUtil;
import ghidra.program.model.pcode.HighSymbol;
import ghidra.program.model.symbol.SourceType;
import java.util.Iterator;
import java.util.LinkedHashSet;
import java.util.Set;

public class InferCalleeParams extends GhidraScript {

    private long protectedLo = 0x01dd9000L;
    private long protectedHi = 0x02c98000L;

    private boolean isProtected(Address a) {
        long v = a.getOffset();
        return v >= protectedLo && v < protectedHi;
    }


    /** 一条非调用指令可能流向的地址:getFlows() 加上操作数里直接写出的地址(有的跳转没有建引用)。 */
    private java.util.List<Address> jumpTargets(Instruction ins) {
        java.util.List<Address> out = new java.util.ArrayList<>();
        Address[] flows = ins.getFlows();
        if (flows != null) {
            for (Address t : flows) {
                if (t != null) out.add(t);
            }
        }
        if ((ins.getFlowType().isJump() || ins.getFlowType().isTerminal()) && ins.getNumOperands() > 0) {
            Address a0 = ins.getAddress(0);
            if (a0 != null) out.add(a0);
            for (Object o : ins.getOpObjects(0)) {
                if (o instanceof Address) {
                    out.add((Address) o);
                } else if (o instanceof ghidra.program.model.scalar.Scalar) {
                    long v = ((ghidra.program.model.scalar.Scalar) o).getUnsignedValue();
                    out.add(ins.getAddress().getNewAddress(v));
                }
            }
        }
        return out;
    }

    /** 函数本身在受保护区域,或有"跳"(不是调用)进受保护区域的指令。 */
    private boolean touchesProtected(Function f) {
        if (isProtected(f.getEntryPoint())) {
            return true;
        }
        InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            // 普通调用只是引用地址,反编译器不会把被调用者并进来;但"调用并终止"(尾跳,如跳板)要算
            if (ins.getFlowType().isCall() && !ins.getFlowType().isTerminal()) {
                continue;
            }
            for (Address t : jumpTargets(ins)) {
                if (isProtected(t)) {
                    return true;
                }
            }
        }
        return false;
    }

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("用法: InferCalleeParams.java <目标函数地址> [<目标函数地址> ...]");
            return;
        }
        String lo = System.getenv("DNF_PROTECTED_LO");
        String hi = System.getenv("DNF_PROTECTED_HI");
        if (lo != null && !lo.isEmpty()) protectedLo = Long.parseLong(lo.trim(), 16);
        if (hi != null && !hi.isEmpty()) protectedHi = Long.parseLong(hi.trim(), 16);

        FunctionManager fm = currentProgram.getFunctionManager();
        Set<Address> callees = new LinkedHashSet<>();
        for (String a : args) {
            Function f = fm.getFunctionAt(toAddr(a));
            if (f == null) {
                printerr("没有函数: " + a);
                continue;
            }
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                if (!ins.getFlowType().isCall()) continue;
                Address[] flows = ins.getFlows();
                if (flows == null) continue;
                for (Address t : flows) {
                    if (t != null) callees.add(t);
                }
            }
        }
        println("直接被调用者: " + callees.size());

        DecompileOptions options = new DecompileOptions();
        DecompInterface plain = new DecompInterface();
        plain.setOptions(options);
        plain.toggleCCode(false);
        plain.toggleSyntaxTree(false);
        plain.openProgram(currentProgram);
        DecompInterface paramId = new DecompInterface();
        paramId.setOptions(options);
        paramId.toggleCCode(false);
        paramId.toggleSyntaxTree(false);
        paramId.setSimplificationStyle("paramid");
        paramId.openProgram(currentProgram);

        int done = 0, skippedProtected = 0, skippedOther = 0, thiscalls = 0;
        try {
            for (Address t : callees) {
                Function f = fm.getFunctionAt(t);
                if (f == null || f.isExternal()) { println("跳过(无函数或外部): " + t); skippedOther++; continue; }
                if (touchesProtected(f)) {
                    println("跳过(触及受保护区域): " + t);
                    skippedProtected++;
                    continue;
                }
                if (f.getSignatureSource() != SourceType.DEFAULT) { println("跳过(已有签名): " + t); skippedOther++; continue; }

                DecompileResults r = plain.decompileFunction(f, 90, monitor);
                if (r == null || !r.decompileCompleted() || r.getHighFunction() == null) {
                    println("跳过(普通模式反编译失败): " + t);
                    skippedOther++;
                    continue;
                }
                boolean needsThis = false;
                Iterator<HighSymbol> syms = r.getHighFunction().getLocalSymbolMap().getSymbols();
                while (syms.hasNext()) {
                    HighSymbol s = syms.next();
                    if (s.getName().equals("in_ECX")) needsThis = true;
                }
                if (needsThis) {
                    try {
                        f.setCallingConvention("__thiscall");
                        thiscalls++;
                    } catch (Exception e) {
                        println("设置 __thiscall 失败 " + t + ": " + e.getMessage());
                    }
                }
                r = paramId.decompileFunction(f, 90, monitor);
                if (r == null || !r.decompileCompleted() || r.getHighFunction() == null) {
                    println("跳过(paramid 模式反编译失败): " + t);
                    skippedOther++;
                    continue;
                }
                HighFunction hf = r.getHighFunction();
                HighFunctionDBUtil.commitParamsToDatabase(hf, true,
                    HighFunctionDBUtil.ReturnCommitOption.COMMIT, SourceType.ANALYSIS);
                done++;
            }
        } finally {
            plain.dispose();
            paramId.dispose();
        }
        println("已推断 " + done + " 个(其中 __thiscall " + thiscalls + "),跳过受保护 " + skippedProtected
            + ",跳过其它 " + skippedOther);
    }
}
