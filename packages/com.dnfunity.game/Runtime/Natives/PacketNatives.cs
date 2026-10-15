using System.Collections.Generic;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 随消息发送的二进制数据:写入端(sq_BinaryStartWrite、sq_BinaryWriteDword、obj.sq_StartWrite、obj.sq_WriteWord ……)与读取端(reciveData.readDword() ……)。
    /// 写入端是世界里的发送缓冲:start 清空,write 追加,发送消息(创建被动对象等)时读出内容(不清空);读取端按同样的顺序与宽度读出。
    /// 三套写法(自由函数 sq_Binary*、对象成员 sq_Write*、sq_binaryData_*)指向同一个普通缓冲;"Parameter"写法是另一个参数缓冲(E-046,客户端两个缓冲的地址不同)。
    /// </summary>
    public sealed partial class GameWorld
    {
        private void RegisterPacketNatives()
        {
            const NativeStatus I = NativeStatus.Inferred;
            const NativeForm M = NativeForm.Member;

            const NativeStatus V = NativeStatus.Verified;

            // ---- 普通发送缓冲:全局写法 sq_Binary*、对象成员写法 sq_* 与 sq_binaryData_*,三套指向同一个缓冲(客户端 0x019F2CB8 起;
            //      起始函数 0x00BB16F0 只把长度置 0,写入函数把值存到"起始地址 + 长度"处再加长度,都不检查容量,E-046)
            string startNote = "清空普通发送缓冲(把长度置 0),开始写一段随下一条消息发出的二进制数据(客户端 0x00bb16f0)";
            DefineFree("sq_BinaryStartWrite", V, startNote, c =>
            {
                StartWrite();
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_StartWrite", "sq_binaryData_startWrite" })
            {
                Define(n, V, startNote, M, c =>
                {
                    StartWrite();
                    return c.ReturnNothing();
                });
            }

            // ---- 参数缓冲:另一个缓冲(客户端 0x019DFD44 起),只有 sq_CreatePassiveObjectAfterWarning 读它(固定 8 个 Dword,见 0x005acb00)
            DefineFree("sq_BinaryParameterStartWrite", V,
                "清空参数缓冲(客户端 0x005aca00)。这是与普通发送缓冲不同的另一个缓冲,不要混用", c =>
                {
                    StartParameterWrite();
                    return c.ReturnNothing();
                });

            var widths = new[] { new KeyValuePair<string, int>("Byte", 1), new KeyValuePair<string, int>("Word", 2), new KeyValuePair<string, int>("Dword", 4), new KeyValuePair<string, int>("Bool", 1) };
            foreach (KeyValuePair<string, int> kv in widths)
            {
                int width = kv.Value;
                string note = "往普通发送缓冲追加一个 " + kv.Key + "(" + width + " 字节,小端;布尔与字节共用同一个实现)";
                DefineFree("sq_BinaryWrite" + kv.Key, V, note, c =>
                {
                    WriteInt(c.IntOr(0, 0), width);
                    return c.ReturnNothing();
                });
                DefineFree("sq_BinaryParameterWrite" + kv.Key, V,
                    "往参数缓冲追加一个 " + kv.Key + "(" + width + " 字节,小端;不是普通发送缓冲)", c =>
                    {
                        WriteParameterInt(c.IntOr(0, 0), width);
                        return c.ReturnNothing();
                    });
                foreach (string n in new[] { "sq_Write" + kv.Key, "sq_binaryData_write" + kv.Key })
                {
                    Define(n, V, note, M, c =>
                    {
                        WriteInt(c.IntOr(0, 0), width);
                        return c.ReturnNothing();
                    });
                }
            }
            string floatNote = "往普通发送缓冲追加一个 Float(4 字节单精度,小端)";
            DefineFree("sq_BinaryWriteFloat", V, floatNote, c =>
            {
                WriteFloat(c.FloatOr(0, 0f));
                return c.ReturnNothing();
            });
            DefineFree("sq_BinaryParameterWriteFloat", V, "往参数缓冲追加一个 Float(4 字节单精度,小端;不是普通发送缓冲)", c =>
            {
                WriteParameterFloat(c.FloatOr(0, 0f));
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_WriteFloat", "sq_binaryData_writeFloat" })
            {
                Define(n, V, floatNote, M, c =>
                {
                    WriteFloat(c.FloatOr(0, 0f));
                    return c.ReturnNothing();
                });
            }

            // ---- 读取端
            // 读取端:客户端从"数据起始地址 + 游标"处读出对应宽度、游标前进,不检查是否越界(越界会读到数据后面的内存,是未定义的);
            // 这里越界返回 0,是有意的差别。实现:readByte 0x005abb70、readBool 0x005abb50、readWord 0x005abb90、readDword 0x005abbb0、readFloat 0x005abbd0、getSize 0x011a4790
            Define("readByte", V, "读一个 Byte(无符号);客户端不检查越界,这里越界返回 0(有意的差别)", M, c => c.Return(c.SelfAs<PacketReader>().ReadByte()));
            Define("readWord", I, "读一个 Word(2 字节);客户端读 2 字节,按有符号还是无符号交给脚本取决于返回值转换,没有读到,这里按无符号;越界返回 0(客户端不检查)", M, c => c.Return(c.SelfAs<PacketReader>().ReadWord()));
            Define("readDword", V, "读一个 Dword(有符号 32 位:脚本用它传可为负的坐标);客户端不检查越界,这里越界返回 0(有意的差别)", M, c => c.Return(c.SelfAs<PacketReader>().ReadDword()));
            Define("readFloat", V, "读一个 Float(单精度);客户端不检查越界,这里越界返回 0(有意的差别)", M, c => c.Return(c.SelfAs<PacketReader>().ReadFloat()));
            Define("readBool", V, "读一个 Bool(1 字节,非 0 为真);客户端不检查越界,这里越界返回假(有意的差别)", M, c => c.Return(c.SelfAs<PacketReader>().ReadBool()));
            Define("getSize", V, "读取端的数据总字节数(客户端 0x011a4790 返回对象开头存的长度)", M, c => c.Return(c.SelfAs<PacketReader>().Size));
            DefineFree("sq_BinaryGetReadSize", I, "最近一次交给脚本的读取端已经读了多少字节", c => c.Return(LastReader == null ? 0 : LastReader.Position));
        }
    }
}
