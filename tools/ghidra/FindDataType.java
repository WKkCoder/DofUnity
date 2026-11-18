// 在已导入程序的数据类型管理器里按名字查找结构体/枚举,打印成员(偏移、类型、名字)。只读。
// 用法(脚本参数):<输出文件> <正则 1> [<正则 2> ...]   (对类型名做 find 匹配)
// 结构体的 DWARF 来源由 Ghidra 的 DWARF 分析器导入;若工程里没有该类型则无输出。
import ghidra.app.script.GhidraScript;
import ghidra.program.model.data.*;
import java.io.PrintWriter;
import java.util.Iterator;
import java.util.regex.Pattern;

public class FindDataType extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            println("用法: FindDataType.java <输出文件> <正则>...");
            return;
        }
        PrintWriter out = new PrintWriter(args[0], "UTF-8");
        DataTypeManager dtm = currentProgram.getDataTypeManager();
        for (int i = 1; i < args.length; i++) {
            Pattern p = Pattern.compile(args[i]);
            out.println("## 模式 " + args[i]);
            Iterator<DataType> it = dtm.getAllDataTypes();
            int n = 0;
            while (it.hasNext()) {
                DataType dt = it.next();
                if (!p.matcher(dt.getName()).find()) continue;
                if (dt instanceof Structure) {
                    Structure s = (Structure) dt;
                    out.println("struct " + dt.getPathName() + " size=" + s.getLength());
                    for (DataTypeComponent c : s.getComponents()) {
                        out.println(String.format("    +%-6x %-40s %s (len %d)", c.getOffset(),
                            c.getDataType().getName(), c.getFieldName(), c.getLength()));
                    }
                    n++;
                } else if (dt instanceof ghidra.program.model.data.Enum) {
                    ghidra.program.model.data.Enum e = (ghidra.program.model.data.Enum) dt;
                    out.println("enum " + dt.getPathName() + " size=" + e.getLength());
                    for (String nm : e.getNames()) {
                        out.println(String.format("    %-48s = %d", nm, e.getValue(nm)));
                    }
                    n++;
                } else if (dt instanceof Union) {
                    out.println("union " + dt.getPathName() + " size=" + dt.getLength());
                    n++;
                } else if (dt instanceof TypeDef) {
                    out.println("typedef " + dt.getPathName() + " -> " + ((TypeDef) dt).getBaseDataType().getName());
                    n++;
                }
            }
            out.println("# 命中 " + n);
        }
        out.close();
    }
}
