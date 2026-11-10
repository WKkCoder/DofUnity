using System;
using System.Buffers.Binary;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 解释执行 CSquare 单块直线例程（微程序源自 0x202e10 / 0x203c10）。
    /// </summary>
    internal static class AntibotSquareVm
    {
        private const int MemSize = 1 << 20;
        private const int VmThis = 0x50000;
        private const int TableBase = 0x80000;
        private const int StateScratch = 0x70000;

        internal static void RunEncrypt(uint[] stateWords, uint[] keyWords)
        {
            RunProgram(AntibotSquareUcode.EncryptProgram, stateWords, keyWords);
        }

        internal static void RunDecrypt(uint[] stateWords, uint[] keyWords)
        {
            RunProgram(AntibotSquareUcode.DecryptProgram, stateWords, keyWords);
        }

        private static void RunProgram(byte[] program, uint[] stateWords, uint[] keyWords)
        {
            byte[] mem = new byte[MemSize];
            LayOutTables(mem);
            int stateAddr = StateScratch;
            for (int i = 0; i < 4; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(stateAddr + i * 4), stateWords[i]);
            }

            int keysAddr = stateAddr + 0x1000;
            for (int i = 0; i < keyWords.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(keysAddr + i * 4), keyWords[i]);
            }

            uint[] r = new uint[8];
            SetupCall(r, mem, VmThis, stateAddr, keysAddr);
            Execute(program, r, mem);
            for (int i = 0; i < 4; i++)
            {
                stateWords[i] = BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(stateAddr + i * 4));
            }
        }

        private static void LayOutTables(byte[] mem)
        {
            int[] offs = { 0, 4, 0x10, 0x14, 0x18, 0x1C, 0x20, 0x24, 0x28, 0x2C };
            byte[][] blobs =
            {
                AntibotSquareTables.SBox,
                AntibotSquareTables.InvSBox,
                PackTable(AntibotSquareTables.Te0),
                PackTable(AntibotSquareTables.Te1),
                PackTable(AntibotSquareTables.Te2),
                PackTable(AntibotSquareTables.Te3),
                PackTable(AntibotSquareTables.Td0),
                PackTable(AntibotSquareTables.Td1),
                PackTable(AntibotSquareTables.Td2),
                PackTable(AntibotSquareTables.Td3),
            };
            for (int i = 0; i < offs.Length; i++)
            {
                int where = TableBase + offs[i] * 0x800;
                Buffer.BlockCopy(blobs[i], 0, mem, where, blobs[i].Length);
                BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(VmThis + offs[i]), (uint)where);
            }
        }

        private static byte[] PackTable(uint[] tab)
        {
            byte[] b = new byte[1024];
            for (int i = 0; i < 256; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), tab[i]);
            }

            return b;
        }

        private static void SetupCall(uint[] r, byte[] mem, int obj, int state, int keys)
        {
            int esp = 0xE0000;
            esp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(esp), (uint)keys);
            esp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(esp), (uint)state);
            esp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(esp), (uint)obj);
            esp -= 4;
            BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(esp), 0xDEADBEEFu);
            r[7] = (uint)esp;
        }

        private static void Execute(byte[] prog, uint[] r, byte[] mem)
        {
            int ip = 0;
            while (ip < prog.Length)
            {
                byte op = prog[ip++];
                switch (op)
                {
                    case 0:
                        return;
                    case 1:
                        r[7] -= 4;
                        WriteU32(mem, (int)r[7], r[prog[ip++]]);
                        break;
                    case 2:
                        r[prog[ip++]] = ReadU32(mem, (int)r[7]);
                        r[7] += 4;
                        break;
                    case 3:
                    {
                        int dr = prog[ip++];
                        int sr = prog[ip++];
                        r[dr] = r[sr];
                        break;
                    }
                    case 4:
                    {
                        int dr = prog[ip++];
                        r[dr] = ReadU32(prog, ref ip);
                        break;
                    }
                    case 5:
                    {
                        int dr = prog[ip++];
                        ReadMemTok(prog, ref ip, out int b, out int ix, out int sc, out uint disp);
                        r[dr] = ReadMem32(r, mem, b, ix, sc, disp);
                        break;
                    }
                    case 6:
                    {
                        ReadMemTok(prog, ref ip, out int b, out int ix, out int sc, out uint disp);
                        int sr = prog[ip++];
                        WriteMem32(r, mem, b, ix, sc, disp, r[sr]);
                        break;
                    }
                    case 7:
                    {
                        int dr = prog[ip++];
                        int sr = prog[ip++];
                        r[dr] ^= r[sr];
                        break;
                    }
                    case 8:
                    {
                        int dr = prog[ip++];
                        ReadMemTok(prog, ref ip, out int b, out int ix, out int sc, out uint disp);
                        r[dr] ^= ReadMem32(r, mem, b, ix, sc, disp);
                        break;
                    }
                    case 9:
                    {
                        ReadMemTok(prog, ref ip, out int b, out int ix, out int sc, out uint disp);
                        int sr = prog[ip++];
                        uint addr = ResolveAddr(r, b, ix, sc, disp);
                        WriteU32(mem, (int)addr, ReadU32(mem, (int)addr) ^ r[sr]);
                        break;
                    }
                    case 10:
                    case 11:
                    {
                        int dr = prog[ip++];
                        byte srcKind = prog[ip++];
                        uint add = srcKind switch
                        {
                            0 => ReadU32(prog, ref ip),
                            1 => r[prog[ip++]],
                            _ => ReadMem32(r, mem, prog, ref ip),
                        };
                        if (op == 10)
                        {
                            r[dr] = r[dr] + add;
                        }
                        else
                        {
                            r[dr] = r[dr] - add;
                        }

                        break;
                    }
                    case 12:
                    case 14:
                    {
                        int dr = prog[ip++];
                        int cnt = prog[ip++];
                        if (op == 12)
                        {
                            r[dr] >>= cnt;
                        }
                        else
                        {
                            r[dr] = (r[dr] << cnt);
                        }

                        break;
                    }
                    case 13:
                    case 15:
                    {
                        ReadMemTok(prog, ref ip, out int b, out int ix, out int sc, out uint disp);
                        int cnt = prog[ip++];
                        uint addr = ResolveAddr(r, b, ix, sc, disp);
                        uint v = ReadU32(mem, (int)addr);
                        if (op == 13)
                        {
                            v >>= cnt;
                        }
                        else
                        {
                            v <<= cnt;
                        }

                        WriteU32(mem, (int)addr, v);
                        break;
                    }
                    case 16:
                    {
                        int dr = prog[ip++];
                        ReadMemTok(prog, ref ip, out int b, out int ix, out int sc, out uint disp);
                        r[dr] = ReadMem8(r, mem, b, ix, sc, disp);
                        break;
                    }
                    case 17:
                    {
                        int dr = prog[ip++];
                        int sr = prog[ip++];
                        r[dr] = (r[sr] >> 8) & 0xFFu;
                        break;
                    }
                    default:
                        return;
                }
            }
        }

        private static uint ReadMem32(uint[] r, byte[] mem, byte[] prog, ref int ip)
        {
            ReadMemTok(prog, ref ip, out int b, out int ix, out int sc, out uint disp);
            return ReadMem32(r, mem, b, ix, sc, disp);
        }

        private static void ReadMemTok(byte[] prog, ref int ip, out int b, out int ix, out int sc, out uint disp)
        {
            b = prog[ip++];
            ix = prog[ip++];
            sc = prog[ip++];
            ip++;
            disp = BinaryPrimitives.ReadUInt32LittleEndian(prog.AsSpan(ip));
            ip += 4;
        }

        private static uint ResolveAddr(uint[] r, int b, int ix, int sc, uint disp)
        {
            if (b == 0xFF && ix == 0xFF)
            {
                return disp;
            }

            if (ix != 0xFF)
            {
                return r[b] + (r[ix] * (uint)sc) + disp;
            }

            return r[b] + disp;
        }

        private static uint ReadMem32(uint[] r, byte[] mem, int b, int ix, int sc, uint disp)
        {
            uint addr = ResolveAddr(r, b, ix, sc, disp);
            return ReadU32(mem, (int)addr);
        }

        private static uint ReadMem8(uint[] r, byte[] mem, int b, int ix, int sc, uint disp)
        {
            uint addr = ResolveAddr(r, b, ix, sc, disp);
            return mem[addr];
        }

        private static void WriteMem32(uint[] r, byte[] mem, int b, int ix, int sc, uint disp, uint val)
        {
            uint addr = ResolveAddr(r, b, ix, sc, disp);
            WriteU32(mem, (int)addr, val);
        }

        private static uint ReadU32(byte[] buf, ref int ip)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(ip));
            ip += 4;
            return v;
        }

        private static uint ReadU32(byte[] mem, int addr)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(mem.AsSpan(addr));
        }

        private static void WriteU32(byte[] mem, int addr, uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(mem.AsSpan(addr), v);
        }
    }
}
