// 扫描程序里所有形如 "[标签名]" 的字符串常量,列出引用它们的函数。
// 用来回答:服务端的哪些加载函数认识哪些脚本标签(给 P2 的标签语义目录提供来源)。
//
// 用法(脚本参数:输出文件路径):
//   analyzeHeadless re/ghidra/projects dfgame -process df_game_r.orig -noanalysis -readOnly \
//       -scriptPath tools/ghidra -postScript TagRefs.java re/ghidra/out/server_tag_refs.tsv
//
// 输出 TSV 列:字符串(含方括号)、字符串地址、引用次数、引用它的不同函数个数、函数列表(最多 12 个,
// 形如 "类::函数@入口地址")。只输出名字与地址,不输出反编译代码。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

import java.io.File;
import java.io.PrintWriter;
import java.util.LinkedHashSet;
import java.util.Set;
import java.util.regex.Pattern;

public class TagRefs extends GhidraScript {

    private static final Pattern TAG = Pattern.compile("^\\[/?[^\\[\\]\\n]{1,80}\\]$");

    @Override
    protected void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("需要参数:输出文件路径");
            return;
        }
        File out = new File(args[0]);
        out.getParentFile().mkdirs();
        int strings = 0;
        int tags = 0;
        int referenced = 0;
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            w.println("tag\taddr\trefs\tfuncs\tfunction_list");
            DataIterator it = currentProgram.getListing().getDefinedData(true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Data d = it.next();
                if (!d.hasStringValue()) {
                    continue;
                }
                strings++;
                Object v = d.getValue();
                if (!(v instanceof String)) {
                    continue;
                }
                String s = (String) v;
                if (!TAG.matcher(s).matches()) {
                    continue;
                }
                tags++;
                int refs = 0;
                Set<String> funcs = new LinkedHashSet<>();
                ReferenceIterator ri = currentProgram.getReferenceManager().getReferencesTo(d.getAddress());
                while (ri.hasNext()) {
                    Reference r = ri.next();
                    refs++;
                    Function f = currentProgram.getFunctionManager().getFunctionContaining(r.getFromAddress());
                    if (f != null) {
                        funcs.add(f.getName(true) + "@" + f.getEntryPoint());
                    }
                }
                if (refs > 0) {
                    referenced++;
                }
                StringBuilder sb = new StringBuilder();
                int n = 0;
                for (String f : funcs) {
                    if (n++ >= 12) {
                        sb.append(" ...");
                        break;
                    }
                    if (sb.length() > 0) {
                        sb.append(" | ");
                    }
                    sb.append(f);
                }
                w.println(s + "\t" + d.getAddress() + "\t" + refs + "\t" + funcs.size() + "\t" + sb);
            }
        }
        println("TAGREFS strings=" + strings + " tag_like=" + tags + " referenced=" + referenced + " out=" + out);
    }
}
