// 列出"字符串访问函数"的所有调用点,并尽量还原每个调用点传入的数据块地址。
// 只读:不改程序,不保存;输出只放在 re/ 下(已被 .gitignore 与提交守卫排除)。
//
// 背景(E-041):客户端里的文本不是明文放着的,而是放成"数据块",每次用到时经一个访问函数取出。
// 访问函数有两个:宽字符版(块的头 2 字节为 0xA1 0x2C 时表示明文,否则解码)和窄字符版(头 1 字节为 0xA1 时表示明文)。
//
// 用法(脚本参数):<输出文件> <宽字符访问函数地址> <窄字符访问函数地址>
//   analyzeHeadless re/ghidra/projects dnf -process DNF.exe -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript StringCalls.java re/notes/string_calls.tsv 0119fef0 0119fed0
//
// 输出每行(制表符分隔):调用点 | 所在函数入口 | 访问函数(W 或 N) | 参数形式 | 数据块地址
// 参数形式:
//   push_imm     调用前一条是 PUSH 立即数(最常见)
//   reg_imm      调用前一条是 PUSH 寄存器,往前几条里最近一次给这个寄存器赋的是立即数或固定地址
//   push_imm_gap / reg_imm_gap  同上,但 PUSH 与调用之间夹了几条不动栈的指令
//   unknown      其余(参数是计算出来的、传进来的或从表里取的),数据块地址留空
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.lang.Register;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.listing.Listing;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import java.io.File;
import java.io.PrintWriter;

public class StringCalls extends GhidraScript {

    private static final int BACKTRACK = 10;
    private static final int SKIP_LIMIT = 4;

    // 这条指令会不会改变栈上的内容或栈指针,或改变控制流(这样的指令挡住往前找)
    private boolean touchesStack(Instruction in) {
        String m = in.getMnemonicString();
        if ("POP".equals(m) || "CALL".equals(m) || "RET".equals(m) || "LEAVE".equals(m)) {
            return true;
        }
        if (in.getFlowType().isJump() || in.getFlowType().isTerminal() || in.getFlowType().isCall()) {
            return true;
        }
        if (("ADD".equals(m) || "SUB".equals(m)) && in.getNumOperands() > 0) {
            Register r = in.getRegister(0);
            return r != null && "ESP".equals(r.getName());
        }
        return false;
    }

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 3) {
            printerr("用法: StringCalls.java <输出文件> <宽字符访问函数地址> <窄字符访问函数地址>");
            return;
        }
        File out = new File(args[0]);
        out.getParentFile().mkdirs();
        Listing listing = currentProgram.getListing();
        String[] kinds = {"W", "N"};
        long[] totals = new long[2];
        long[] resolved = new long[2];
        long nonCall = 0;
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            for (int k = 0; k < 2; k++) {
                Address fn = toAddr(args[1 + k]);
                ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(fn);
                while (it.hasNext()) {
                    Reference ref = it.next();
                    if (!ref.getReferenceType().isCall()) {
                        nonCall++;
                        w.println(ref.getFromAddress() + "\t\t" + kinds[k] + "\tnon_call\t");
                        continue;
                    }
                    Address site = ref.getFromAddress();
                    Instruction call = listing.getInstructionAt(site);
                    Function owner = currentProgram.getFunctionManager().getFunctionContaining(site);
                    String ownerText = owner == null ? "" : owner.getEntryPoint().toString();
                    totals[k]++;
                    String form = "unknown";
                    String blob = "";
                    if (call != null) {
                        // 访问函数只有一个栈参数:取调用前最近一次压栈。中间可以夹几条不动栈的指令
                        // (编译器会把别的赋值插进来),遇到调用、跳转、出栈、改栈指针就不再往前找。
                        Instruction prev = call.getPrevious();
                        int skipped = 0;
                        while (prev != null && skipped < SKIP_LIMIT && !"PUSH".equals(prev.getMnemonicString())
                                && !touchesStack(prev)) {
                            prev = prev.getPrevious();
                            skipped++;
                        }
                        if (prev != null && "PUSH".equals(prev.getMnemonicString())) {
                            Scalar s = prev.getScalar(0);
                            if (s != null) {
                                form = skipped == 0 ? "push_imm" : "push_imm_gap";
                                blob = Long.toHexString(s.getUnsignedValue());
                            } else {
                                Register reg = prev.getRegister(0);
                                if (reg != null) {
                                    String r = backtrack(prev, reg);
                                    if (r != null) {
                                        form = skipped == 0 ? "reg_imm" : "reg_imm_gap";
                                        blob = r;
                                    }
                                }
                            }
                        }
                    }
                    if (!blob.isEmpty()) {
                        resolved[k]++;
                    }
                    w.println(site + "\t" + ownerText + "\t" + kinds[k] + "\t" + form + "\t" + blob);
                }
            }
        }
        println("宽字符版调用点 " + totals[0] + "(还原出块地址 " + resolved[0] + "),窄字符版 " + totals[1]
                + "(还原出 " + resolved[1] + "),非调用引用 " + nonCall);
    }

    // 往前找最近一次给 reg 赋值的指令;只认 MOV reg,imm 与 LEA reg,[固定地址]
    private String backtrack(Instruction from, Register reg) {
        Instruction cur = from.getPrevious();
        for (int i = 0; i < BACKTRACK && cur != null; i++, cur = cur.getPrevious()) {
            String m = cur.getMnemonicString();
            if (cur.getFlowType().isJump() || cur.getFlowType().isTerminal()) {
                return null;
            }
            Register dst = cur.getNumOperands() > 0 ? cur.getRegister(0) : null;
            if (dst != null && dst.equals(reg)) {
                if ("MOV".equals(m)) {
                    Scalar s = cur.getScalar(1);
                    return s == null ? null : Long.toHexString(s.getUnsignedValue());
                }
                if ("LEA".equals(m)) {
                    Object[] objs = cur.getOpObjects(1);
                    if (objs.length == 1 && objs[0] instanceof Address) {
                        return ((Address) objs[0]).toString().replaceFirst("^0+", "");
                    }
                    if (objs.length == 1 && objs[0] instanceof Scalar) {
                        return Long.toHexString(((Scalar) objs[0]).getUnsignedValue());
                    }
                    return null;
                }
                return null;
            }
        }
        return null;
    }
}
