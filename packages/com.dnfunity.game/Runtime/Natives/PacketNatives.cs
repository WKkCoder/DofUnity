using System.Collections.Generic;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>
    /// 随消息发送的二进制数据:写入端(sq_BinaryStartWrite、sq_BinaryWriteDword、obj.sq_StartWrite、obj.sq_WriteWord ……)与读取端(reciveData.readDword() ……)。
    /// 写入端是世界里唯一的一个发送缓冲:start 清空,write 追加,发送消息(创建被动对象等)时取走;读取端按同样的顺序与宽度读出。
    /// 两套写法(自由函数 sq_Binary*、对象成员 sq_Write* / sq_binaryData_*)等价——依据是脚本里两种写法的数据被同一个 reciveData.read* 读出(推断)。
    /// </summary>
    public sealed partial class GameWorld
    {
        private void RegisterPacketNatives()
        {
            const NativeStatus I = NativeStatus.Inferred;
            const NativeForm M = NativeForm.Member;

            string startNote = "清空发送缓冲,开始写一段随下一条消息发出的二进制数据";
            DefineFree("sq_BinaryStartWrite", I, startNote, c =>
            {
                StartWrite();
                return c.ReturnNothing();
            });
            DefineFree("sq_BinaryParameterStartWrite", I, startNote + "(Parameter 写法:与普通写法共用同一个缓冲,推断)", c =>
            {
                StartWrite();
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_StartWrite", "sq_binaryData_startWrite" })
            {
                Define(n, I, startNote, M, c =>
                {
                    StartWrite();
                    return c.ReturnNothing();
                });
            }

            var widths = new[] { new KeyValuePair<string, int>("Byte", 1), new KeyValuePair<string, int>("Word", 2), new KeyValuePair<string, int>("Dword", 4), new KeyValuePair<string, int>("Bool", 1) };
            foreach (KeyValuePair<string, int> kv in widths)
            {
                int width = kv.Value;
                string note = "往发送缓冲追加一个 " + kv.Key + "(" + width + " 字节,小端)";
                DefineFree("sq_BinaryWrite" + kv.Key, I, note, c =>
                {
                    WriteInt(c.IntOr(0, 0), width);
                    return c.ReturnNothing();
                });
                DefineFree("sq_BinaryParameterWrite" + kv.Key, I, note, c =>
                {
                    WriteInt(c.IntOr(0, 0), width);
                    return c.ReturnNothing();
                });
                foreach (string n in new[] { "sq_Write" + kv.Key, "sq_binaryData_write" + kv.Key })
                {
                    Define(n, I, note, M, c =>
                    {
                        WriteInt(c.IntOr(0, 0), width);
                        return c.ReturnNothing();
                    });
                }
            }
            string floatNote = "往发送缓冲追加一个 Float(4 字节单精度,小端)";
            DefineFree("sq_BinaryWriteFloat", I, floatNote, c =>
            {
                WriteFloat(c.FloatOr(0, 0f));
                return c.ReturnNothing();
            });
            DefineFree("sq_BinaryParameterWriteFloat", I, floatNote, c =>
            {
                WriteFloat(c.FloatOr(0, 0f));
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_WriteFloat", "sq_binaryData_writeFloat" })
            {
                Define(n, I, floatNote, M, c =>
                {
                    WriteFloat(c.FloatOr(0, 0f));
                    return c.ReturnNothing();
                });
            }

            // ---- 读取端
            Define("readByte", I, "读一个 Byte(无符号);越界返回 0", M, c => c.Return(c.SelfAs<PacketReader>().ReadByte()));
            Define("readWord", I, "读一个 Word(无符号 16 位,符号是推断);越界返回 0", M, c => c.Return(c.SelfAs<PacketReader>().ReadWord()));
            Define("readDword", I, "读一个 Dword(有符号 32 位:脚本用它传可为负的坐标);越界返回 0", M, c => c.Return(c.SelfAs<PacketReader>().ReadDword()));
            Define("readFloat", I, "读一个 Float;越界返回 0", M, c => c.Return(c.SelfAs<PacketReader>().ReadFloat()));
            Define("readBool", I, "读一个 Bool(1 字节)", M, c => c.Return(c.SelfAs<PacketReader>().ReadBool()));
            Define("getSize", I, "读取端的数据总字节数", M, c => c.Return(c.SelfAs<PacketReader>().Size));
            DefineFree("sq_BinaryGetReadSize", I, "最近一次交给脚本的读取端已经读了多少字节", c => c.Return(LastReader == null ? 0 : LastReader.Position));
        }
    }
}
