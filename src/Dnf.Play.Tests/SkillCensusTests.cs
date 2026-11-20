using System.Text;
using Dnf.Cli.Analysis;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Game.Tests;
using Xunit;

namespace Dnf.Play.Tests;

/// <summary>
/// <see cref="SkillCensus"/> 的合成单元测试:覆盖 PE 三节解析边界、x86 解码分支、原生态/起手提取与报表脱敏逐行格式。
/// 全程只使用合成编号、合成脚本与合成 3 节 PE 字节,不含任何真实职业或技能数据。
/// </summary>
public sealed class SkillCensusTests
{
    private sealed class SyntheticPeBuilder
    {
        public const uint ImageBase = 0x00400000u;
        public const uint TextRva = 0x00001000u;
        public const uint RdataRva = 0x00200000u;
        public const uint DataRva = 0x00220000u;
        public const uint ProtRva = 0x00240000u;

        public byte[] Text = new byte[0x140000];
        public byte[] Rdata = new byte[0x8000];
        public byte[] Data = new byte[0x2000];
        public byte[] Prot = new byte[0x1000];

        public SyntheticPeBuilder()
        {
            Array.Fill(Text, (byte)0xCC);
        }

        public uint TextVa(int off) => ImageBase + TextRva + (uint)off;
        public uint RdataVa(int off) => ImageBase + RdataRva + (uint)off;
        public uint DataVa(int off) => ImageBase + DataRva + (uint)off;
        public uint ProtVa(int off) => ImageBase + ProtRva + (uint)off;

        public void WriteU32(byte[] sec, int off, uint val)
        {
            BitConverter.GetBytes(val).CopyTo(sec, off);
        }

        public int EmitCallRel32(int off, uint targetVa)
        {
            uint srcVa = TextVa(off);
            int rel = unchecked((int)(targetVa - (srcVa + 5)));
            Text[off] = 0xE8;
            BitConverter.GetBytes(rel).CopyTo(Text, off + 1);
            return off + 5;
        }

        public int EmitJmpRel32(int off, uint targetVa)
        {
            uint srcVa = TextVa(off);
            int rel = unchecked((int)(targetVa - (srcVa + 5)));
            Text[off] = 0xE9;
            BitConverter.GetBytes(rel).CopyTo(Text, off + 1);
            return off + 5;
        }

        public int EmitJeRel8(int off, int targetOff)
        {
            int rel = targetOff - (off + 2);
            Text[off] = 0x74;
            Text[off + 1] = unchecked((byte)(sbyte)rel);
            return off + 2;
        }

        public int EmitMovEaxImm(int off, uint imm)
        {
            Text[off] = 0xB8;
            BitConverter.GetBytes(imm).CopyTo(Text, off + 1);
            return off + 5;
        }

        public int EmitMovEdxImm(int off, uint imm)
        {
            Text[off] = 0xBA;
            BitConverter.GetBytes(imm).CopyTo(Text, off + 1);
            return off + 5;
        }

        public int EmitMovEaxMemDisp32(int off, uint va)
        {
            // mov eax, [va] => A1 disp32
            Text[off] = 0xA1;
            BitConverter.GetBytes(va).CopyTo(Text, off + 1);
            return off + 5;
        }

        public int EmitPushImm(int off, int imm)
        {
            if (imm is >= -128 and <= 127)
            {
                Text[off] = 0x6A;
                Text[off + 1] = unchecked((byte)(sbyte)imm);
                return off + 2;
            }
            Text[off] = 0x68;
            BitConverter.GetBytes(imm).CopyTo(Text, off + 1);
            return off + 5;
        }

        public int EmitSetState(int off, int stateId)
        {
            // mov edx, [eax + 0x30c]; push stateId; call edx
            Text[off] = 0x8B;
            Text[off + 1] = 0x90;
            BitConverter.GetBytes(0x30Cu).CopyTo(Text, off + 2);
            off += 6;
            off = EmitPushImm(off, stateId);
            Text[off] = 0xFF;
            Text[off + 1] = 0xD2;
            return off + 2;
        }

        public int EmitRet(int off)
        {
            Text[off] = 0xC3;
            return off + 1;
        }

        public int EmitMovEdxSetStateSlot(int off)
        {
            // mov edx, [eax + 0x30c] => 8B 90 0C 03 00 00(与 EmitSetState 的前半段同形,但后面不一定有 push)
            Text[off] = 0x8B;
            Text[off + 1] = 0x90;
            BitConverter.GetBytes(0x30Cu).CopyTo(Text, off + 2);
            return off + 6;
        }

        public int EmitCallEdx(int off)
        {
            // call edx => FF D2
            Text[off] = 0xFF;
            Text[off + 1] = 0xD2;
            return off + 2;
        }

        public int EmitAddRegImm8(int off, byte modrm, sbyte imm)
        {
            // add r32, imm8 => 83 /0 ib(modrm 的 mod=3、reg=0、rm=目标寄存器:esp=C4、eax=C0)
            Text[off] = 0x83;
            Text[off + 1] = modrm;
            Text[off + 2] = unchecked((byte)imm);
            return off + 3;
        }

        public int EmitStackConst(int off, sbyte disp, int value)
        {
            // mov dword ptr [ebp + disp8], imm32 => C7 45 dd imm32
            Text[off] = 0xC7;
            Text[off + 1] = 0x45;
            Text[off + 2] = unchecked((byte)disp);
            BitConverter.GetBytes(value).CopyTo(Text, off + 3);
            return off + 7;
        }

        public byte[] Build(int sectionCount = 4)
        {
            const int peOff = 0x80;
            const int optSize = 0xE0;
            int secTableOff = peOff + 24 + optSize;
            int hdrEnd = (secTableOff + sectionCount * 40 + 0x1FF) & ~0x1FF;

            int textRaw = hdrEnd;
            int rdataRaw = textRaw + Text.Length;
            int dataRaw = rdataRaw + Rdata.Length;
            int protRaw = dataRaw + Data.Length;
            int totalLen = sectionCount >= 4 ? protRaw + Prot.Length
                : sectionCount == 3 ? dataRaw + Data.Length
                : sectionCount == 2 ? rdataRaw + Rdata.Length
                : sectionCount == 1 ? textRaw + Text.Length
                : hdrEnd;

            var pe = new byte[totalLen];
            pe[0] = (byte)'M';
            pe[1] = (byte)'Z';
            BitConverter.GetBytes(peOff).CopyTo(pe, 0x3C);
            pe[peOff] = (byte)'P';
            pe[peOff + 1] = (byte)'E';
            BitConverter.GetBytes((ushort)sectionCount).CopyTo(pe, peOff + 6);
            BitConverter.GetBytes((ushort)optSize).CopyTo(pe, peOff + 20);
            BitConverter.GetBytes(ImageBase).CopyTo(pe, peOff + 24 + 28);

            void WriteSec(int idx, string name, uint vSize, uint vAddr, uint rSize, uint rPtr)
            {
                int o = secTableOff + idx * 40;
                Encoding.ASCII.GetBytes(name).CopyTo(pe, o);
                BitConverter.GetBytes(vSize).CopyTo(pe, o + 8);
                BitConverter.GetBytes(vAddr).CopyTo(pe, o + 12);
                BitConverter.GetBytes(rSize).CopyTo(pe, o + 16);
                BitConverter.GetBytes(rPtr).CopyTo(pe, o + 20);
                if (rSize > 0)
                {
                    byte[] src = idx == 0 ? Text : idx == 1 ? Rdata : idx == 2 ? Data : Prot;
                    Array.Copy(src, 0, pe, (int)rPtr, (int)rSize);
                }
            }

            if (sectionCount >= 1) WriteSec(0, ".text", (uint)Text.Length, TextRva, (uint)Text.Length, (uint)textRaw);
            if (sectionCount >= 2) WriteSec(1, ".rdata", (uint)Rdata.Length, RdataRva, (uint)Rdata.Length, (uint)rdataRaw);
            if (sectionCount >= 3) WriteSec(2, ".data", (uint)Data.Length, DataRva, (uint)Data.Length, (uint)dataRaw);
            if (sectionCount >= 4) WriteSec(3, ".prot", (uint)Prot.Length, ProtRva, (uint)Prot.Length, (uint)protRaw);
            return pe;
        }
    }

    private static (PvfArchive Pvf, byte[] PeBytes) BuildSyntheticWorld()
    {
        var pe = new SyntheticPeBuilder();

        // 1. 构造 11 张 Character 虚表(间隔 0x500 >= 0x400), 共享 +0x918 按键读取槽与 +0x30c 切状态槽
        uint keyGetterVa = pe.TextVa(0x100);
        pe.EmitRet(0x100);
        uint setStateVa = pe.TextVa(0x120);
        pe.EmitRet(0x120);
        uint keyHelperVa = pe.TextVa(0x140);
        pe.EmitRet(0x140);

        // Class 0 = BaseChar; Class 1 = JobAlpha; Class 2 = JobBeta/Gamma; Class 3 = PerStateJob; Class 4 = BranchAlt; Class 5..10 = filler
        var vtables = new uint[11];
        var procVas = new uint[11];
        var enterVas = new uint[11];
        for (int i = 0; i < 11; i++)
        {
            int vtOff = i * 0x500;
            vtables[i] = pe.RdataVa(vtOff);
            int procOff = 0x10000 + i * 0x18000;
            int enterOff = procOff + 0x200;
            procVas[i] = pe.TextVa(procOff);
            enterVas[i] = pe.TextVa(enterOff);
            pe.EmitRet(procOff);
            pe.EmitRet(enterOff);

            pe.WriteU32(pe.Rdata, vtOff + 0x1F0, procVas[i]);
            pe.WriteU32(pe.Rdata, vtOff + 0x29C, enterVas[i]);
            pe.WriteU32(pe.Rdata, vtOff + 0x30C, setStateVa);
            pe.WriteU32(pe.Rdata, vtOff + 0x918, keyGetterVa);
        }

        // Class 1 (+0xa20) 覆写委托初始化: 将技能 901 映射到处理函数并进入 0x2a
        int delHandlerOff = 0x28400; // 在 Class 1 (0x28000) 的 DelegateInitVa (0x28300) 附近
        int p = delHandlerOff;
        p = pe.EmitSetState(p, 0x2A);
        pe.EmitRet(p);

        int delInitOff = 0x28300;
        pe.WriteU32(pe.Rdata, 1 * 0x500 + 0xA20, pe.TextVa(delInitOff));
        p = delInitOff;
        p = pe.EmitMovEaxImm(p, pe.TextVa(delHandlerOff));
        // mov dword ptr [ebp-8], 901 => C7 45 F8 85 03 00 00
        pe.Text[p] = 0xC7;
        pe.Text[p + 1] = 0x45;
        pe.Text[p + 2] = 0xF8;
        BitConverter.GetBytes(901).CopyTo(pe.Text, p + 3);
        p += 7;
        // call mapInsert (距离 > 0x10000 才触发 ScanDelegateInitializers 匹配)
        uint mapInsertVa = pe.TextVa(0x138000);
        pe.EmitRet(0x138000);
        p = pe.EmitCallRel32(p, mapInsertVa);
        pe.EmitRet(p);

        // 2. 至少 100 处 `+0x918` 后的 `call keyHelperVa`, 以触发 DiscoverKeyHelperVa (>=100 门限)
        int dummyBaseOff = 0x120000;
        for (int k = 0; k < 100; k++)
        {
            int o = dummyBaseOff + k * 36;
            // mov edx, [eax+0x918] => 8B 90 18 09 00 00
            pe.Text[o] = 0x8B;
            pe.Text[o + 1] = 0x90;
            BitConverter.GetBytes(0x918u).CopyTo(pe.Text, o + 2);
            pe.EmitCallRel32(o + 6, keyHelperVa);
        }

        // 3. 构造函数 + 角色工厂跳转表 (4 个职业: 10=Class0+1, 20=Class0+2, 30=Class0+2, 40=Class0+3 与 ConditionalBranch Class4)
        int ctorBaseOff = 0x110000;
        int ctorC1Off = 0x110040;
        int ctorC2Off = 0x110080;
        int ctorC3Off = 0x1100C0;
        int ctorC4Off = 0x110100;

        void EmitCtor(int off, uint vt, uint? baseCtorVa)
        {
            int q = off;
            if (baseCtorVa.HasValue)
            {
                q = pe.EmitCallRel32(q, baseCtorVa.Value);
            }
            q = pe.EmitMovEaxImm(q, vt);
            pe.EmitRet(q);
        }
        EmitCtor(ctorBaseOff, vtables[0], null);
        EmitCtor(ctorC1Off, vtables[1], pe.TextVa(ctorBaseOff));
        EmitCtor(ctorC2Off, vtables[2], pe.TextVa(ctorBaseOff));
        EmitCtor(ctorC3Off, vtables[3], pe.TextVa(ctorBaseOff));
        EmitCtor(ctorC4Off, vtables[4], pe.TextVa(ctorBaseOff));

        int switchAreaOff = 0x110200;
        int tblOff = 0x110300;
        // Case 0 (job 10): call ctorC1
        int c0 = switchAreaOff;
        c0 = pe.EmitCallRel32(c0, pe.TextVa(ctorC1Off));
        pe.EmitRet(c0);
        // Case 1 (job 20): call ctorC2
        int c1 = switchAreaOff + 0x20;
        c1 = pe.EmitCallRel32(c1, pe.TextVa(ctorC2Off));
        pe.EmitRet(c1);
        // Case 2 (job 30): call ctorC2 (同 C++ 类共享)
        int c2 = switchAreaOff + 0x40;
        c2 = pe.EmitCallRel32(c2, pe.TextVa(ctorC2Off));
        pe.EmitRet(c2);
        // Case 3 (job 40): jz -> call ctorC3 else call ctorC4 (触发 ConditionalBranchJobs + >=4 vtables)
        int c3 = switchAreaOff + 0x60;
        int c3Else = c3 + 0x18;
        c3 = pe.EmitJeRel8(c3, c3Else);
        c3 = pe.EmitCallRel32(c3, pe.TextVa(ctorC3Off));
        pe.EmitRet(c3);
        c3Else = pe.EmitCallRel32(c3Else, pe.TextVa(ctorC4Off));
        pe.EmitRet(c3Else);

        pe.WriteU32(pe.Text, tblOff + 0, pe.TextVa(switchAreaOff));
        pe.WriteU32(pe.Text, tblOff + 4, pe.TextVa(switchAreaOff + 0x20));
        pe.WriteU32(pe.Text, tblOff + 8, pe.TextVa(switchAreaOff + 0x40));
        pe.WriteU32(pe.Text, tblOff + 12, pe.TextVa(switchAreaOff + 0x60));

        // 4. 在各个职业类的过程区放入 KeyHub / LoopTable / StackArray / PerStatePointer 起手调用点
        // (a) Class 0 (BaseChar, procOff=0x10000): BaseBuff 循环表 (.data 技能号 [904]) -> 0x11
        pe.WriteU32(pe.Data, 0x00, 904u);
        pe.WriteU32(pe.Data, 0x04, 0u);
        int bSite = 0x10080;
        bSite = pe.EmitMovEaxImm(bSite, pe.DataVa(0x00));
        bSite = pe.EmitPushImm(bSite, 0);
        // push eax (寄存器非已知常量, 落入 rdataIsPtrLoop)
        pe.Text[bSite++] = 0x50;
        int bCall = bSite;
        bSite = pe.EmitCallRel32(bSite, keyHelperVa);
        // cmp eax, -1; je skip
        pe.Text[bSite++] = 0x83; pe.Text[bSite++] = 0xF8; pe.Text[bSite++] = 0xFF;
        int jeAt = bSite;
        bSite += 2;
        bSite = pe.EmitSetState(bSite, 0x11);
        int bSkip = bSite;
        pe.EmitJeRel8(jeAt, bSkip);
        pe.EmitRet(bSite);
        // 确认 bCall 被识别为指针循环
        _ = bCall;

        // (b) Class 1 (procOff=0x28000): KeyHub 技能 902 -> 0x0d; LoopTable(.data) 技能 903 -> 0x0d
        int k1 = 0x28060;
        k1 = pe.EmitPushImm(k1, 902);
        k1 = pe.EmitCallRel32(k1, keyHelperVa);
        pe.Text[k1++] = 0x83; pe.Text[k1++] = 0xF8; pe.Text[k1++] = 0xFF;
        int k1Je = k1; k1 += 2;
        k1 = pe.EmitSetState(k1, 0x0D);
        pe.EmitJeRel8(k1Je, k1);
        pe.EmitRet(k1);

        pe.WriteU32(pe.Data, 0x20, 903u);
        pe.WriteU32(pe.Data, 0x24, 0u);
        int l1 = 0x28100;
        l1 = pe.EmitMovEdxImm(l1, pe.DataVa(0x20));
        pe.Text[l1++] = 0x52; // push edx
        l1 = pe.EmitCallRel32(l1, keyHelperVa);
        pe.Text[l1++] = 0x83; pe.Text[l1++] = 0xF8; pe.Text[l1++] = 0xFF;
        int l1Je = l1; l1 += 2;
        l1 = pe.EmitSetState(l1, 0x0D);
        pe.EmitJeRel8(l1Je, l1);
        pe.EmitRet(l1);

        // (c) Class 2 (procOff=0x40000): KeyHub 911 -> 0x0d, 912 -> 0x2a, 921 -> 0x32, 栈常量数组 [914,915,916] -> 0x11
        void EmitKeyHubSite(int off, int skillId, int stateId)
        {
            int q = off;
            q = pe.EmitPushImm(q, skillId);
            q = pe.EmitCallRel32(q, keyHelperVa);
            pe.Text[q++] = 0x83; pe.Text[q++] = 0xF8; pe.Text[q++] = 0xFF;
            int je = q; q += 2;
            q = pe.EmitSetState(q, stateId);
            pe.EmitJeRel8(je, q);
            pe.EmitRet(q);
        }
        EmitKeyHubSite(0x40060, 911, 0x0D);
        EmitKeyHubSite(0x400C0, 912, 0x2A);
        EmitKeyHubSite(0x40120, 921, 0x32);

        // 栈常量数组 [ebp-0x20], [ebp-0x1c], [ebp-0x18] = [914, 915, 916]
        int s2 = 0x40240;
        void EmitStackConst(int disp, int val)
        {
            pe.Text[s2++] = 0xC7;
            pe.Text[s2++] = 0x45;
            pe.Text[s2++] = unchecked((byte)(sbyte)disp);
            BitConverter.GetBytes(val).CopyTo(pe.Text, s2);
            s2 += 4;
        }
        EmitStackConst(-0x20, 914);
        EmitStackConst(-0x1C, 915);
        EmitStackConst(-0x18, 916);
        pe.Text[s2++] = 0x50; // push eax
        s2 = pe.EmitCallRel32(s2, keyHelperVa);
        pe.Text[s2++] = 0x83; pe.Text[s2++] = 0xF8; pe.Text[s2++] = 0xFF;
        int s2Je = s2; s2 += 2;
        s2 = pe.EmitSetState(s2, 0x11);
        pe.EmitJeRel8(s2Je, s2);
        pe.EmitRet(s2);

        // (d) Class 3 (procOff=0x58000, enterOff=0x58200): PerStatePointer (+0x70a8) 前序函数 -> 913 -> 0x31
        int psFnOff = 0x57E00; // 紧邻前方以 CC 对齐
        EmitKeyHubSite(psFnOff, 913, 0x31);
        int init3 = 0x58060;
        // 含大偏移量 0x70a8 与指向 psFnOff 的 mov eax, psFnVa
        BitConverter.GetBytes(0x70A8u).CopyTo(pe.Text, init3);
        init3 += 4;
        init3 = pe.EmitMovEaxImm(init3, pe.TextVa(psFnOff));
        pe.EmitRet(init3);

        // 5. 构造合成 PVF(4 个职业: 10, 20, 30, 40; 含 Squirrel + token .chr / .skl + secret 名字检查脱敏)
        var pool = new List<string>();
        byte[] BuildTokens(Action<TokenStreamBuilder> write)
        {
            var tb = new TokenStreamBuilder(pool);
            write(tb);
            return tb.Bytes();
        }
        byte[] BuildList(params (int Key, string Val)[] rows)
        {
            var tb = new TokenStreamBuilder(pool);
            foreach (var (k, v) in rows)
            {
                tb.Int(k).Str(v);
            }
            return tb.Bytes();
        }
        byte[] MakeSkill(string type, bool withCommand, string secretName)
        {
            return BuildTokens(w =>
            {
                w.Tag("[name]").Str(secretName).Tag("[type]").Str(type);
                if (withCommand)
                {
                    w.Tag("[command]").Str("Z");
                }
            });
        }
        byte[] MakeChr(string secretJobName, params string[] growNames)
        {
            return BuildTokens(w =>
            {
                w.Tag("[job]").Str(secretJobName).Tag("[growtype name]");
                foreach (string g in growNames)
                {
                    w.Str(g);
                }
            });
        }

        var files = new List<TestData.PvfFileSpec>
        {
            new("character/character.lst", BuildList(
                (10, "alpha_secret/alpha_secret.chr"),
                (20, "beta_secret/beta_secret.chr"),
                (30, "gamma_secret/gamma_secret.chr"),
                (40, "delta_secret/delta_secret.chr")), 0),
            new("character/alpha_secret/alpha_secret.chr", MakeChr("[alpha secret]", "AlphaBase"), 0),
            new("character/beta_secret/beta_secret.chr", MakeChr("[beta secret]", "BetaBase"), 0),
            new("character/gamma_secret/gamma_secret.chr", MakeChr("[gamma secret]", "GammaBase"), 0),
            new("character/delta_secret/delta_secret.chr", MakeChr("[delta secret]", "DeltaCustomGrow"), 0),

            new("skill/skilllist.lst", BuildList(
                (10, "alpha_skills.lst"),
                (20, "beta_skills.lst"),
                (30, "gamma_skills.lst"),
                (40, "delta_skills.lst")), 0),
            new("skill/alpha_skills.lst", BuildList(
                (901, "alpha/s901.skl"),
                (902, "alpha/s902.skl"),
                (903, "alpha/s903.skl"),
                (904, "alpha/s904.skl"),
                (905, "alpha/s905.skl"),
                (906, "alpha/s906.skl")), 0),
            new("skill/beta_skills.lst", BuildList(
                (911, "beta/s911.skl"),
                (912, "beta/s912.skl"),
                (914, "beta/s914.skl"),
                (915, "beta/s915.skl"),
                (916, "beta/s916.skl"),
                (921, "beta/s921.skl")), 0),
            new("skill/gamma_skills.lst", BuildList(
                (921, "gamma/s921.skl"),
                (922, "gamma/s922.skl"),
                (923, "gamma/s923.skl")), 0),
            new("skill/delta_skills.lst", BuildList(
                (913, "delta/s913.skl"),
                (924, "delta/s924.skl")), 0),

            new("skill/alpha/s901.skl", MakeSkill("[active]", true, "SecretAlphaDispatch"), 0),
            new("skill/alpha/s902.skl", MakeSkill("[active]", true, "SecretAlphaKeyHub"), 0),
            new("skill/alpha/s903.skl", MakeSkill("[active]", false, "SecretAlphaLoop"), 0),
            new("skill/alpha/s904.skl", MakeSkill("[active]", true, "SecretAlphaBaseBuff"), 0),
            new("skill/alpha/s905.skl", MakeSkill("[passive]", false, "SecretAlphaPassive"), 0),
            new("skill/alpha/s906.skl", MakeSkill("[active]", true, "SecretAlphaUnmatched"), 0),

            new("skill/beta/s911.skl", MakeSkill("[active]", true, "SecretBetaThrow"), 0),
            new("skill/beta/s912.skl", MakeSkill("[active]", true, "SecretBetaClash"), 0),
            new("skill/beta/s914.skl", MakeSkill("[active]", true, "SecretBetaStack1"), 0),
            new("skill/beta/s915.skl", MakeSkill("[active]", true, "SecretBetaStack2"), 0),
            new("skill/beta/s916.skl", MakeSkill("[active]", true, "SecretBetaStack3"), 0),
            new("skill/beta/s921.skl", MakeSkill("[active]", true, "SecretBetaShared"), 0),

            new("skill/gamma/s921.skl", MakeSkill("[active]", true, "SecretGammaShared"), 0),
            new("skill/gamma/s922.skl", MakeSkill("[active]", true, "SecretGammaSqActive"), 0),
            new("skill/gamma/s923.skl", MakeSkill("[passive]", false, "SecretGammaSqPassive"), 0),

            new("skill/delta/s913.skl", MakeSkill("[active]", true, "SecretDeltaPerState"), 0),
            new("skill/delta/s924.skl", MakeSkill("[active]", true, "SecretDeltaCustomSq"), 0),

            new("sqr/dnf_enum_header.nut", Encoding.UTF8.GetBytes(
                "JOB_GAMMA <- 30;\n" +
                "STATE_SQ_MIX <- 0x2a;\n" +
                "STATE_SQ_PASSIVE <- 0x35;\n"), 0),
            new("sqr/loadstate.nut", Encoding.UTF8.GetBytes(
                "sq_RunScript(\"gamma_sq.nut\");\n" +
                "sq_RunScript(\"deltacustomgrow_sq.nut\");\n"), 0),
            new("sqr/gamma_sq.nut", Encoding.UTF8.GetBytes(
                "IRDSQRCharacter.pushState(JOB_GAMMA, \"a.nut\", \"A\", STATE_SQ_MIX, 922);\n" +
                "IRDSQRCharacter.pushState(JOB_GAMMA, \"b.nut\", \"B\", STATE_SQ_PASSIVE, 923);\n"), 0),
            new("sqr/deltacustomgrow_sq.nut", Encoding.UTF8.GetBytes(
                "pushScriptFiles(\"custom_hdr.nut\");\n" +
                "CustomSubObj.pushState(\"c.nut\", \"C\", CUSTOM_STATE_HEX, 924);\n"), 0),
            new("sqr/custom_hdr.nut", Encoding.UTF8.GetBytes(
                "CUSTOM_STATE_HEX <- 0x36;\n"), 0),
        };

        byte[] strBin = new TokenStreamBuilder(pool).StringTableBytes();
        files.Insert(0, new TestData.PvfFileSpec("stringtable.bin", strBin, 0));

        (byte[] pvfBytes, _) = TestData.BuildPvf(files, validChecksums: true);
        return (new PvfArchive(new MemoryStream(pvfBytes)), pe.Build(sectionCount: 4));
    }

    [Fact]
    public void PeImage_TryParse_RejectsMalformedAndIgnoresProtectedSections()
    {
        Assert.Null(SkillCensus.PeImage.TryParse(new byte[0xFF]));
        // 正好 0x100 字节的最小有效 0 节 PE 不应因长度下界误拒
        byte[] minLenPe = new byte[0x100];
        minLenPe[0] = (byte)'M'; minLenPe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(minLenPe, 0x3C);
        minLenPe[0x80] = (byte)'P'; minLenPe[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)32).CopyTo(minLenPe, 0x80 + 20);
        Assert.NotNull(SkillCensus.PeImage.TryParse(minLenPe));

        byte[] badMz = new byte[0x200];
        Assert.Null(SkillCensus.PeImage.TryParse(badMz));

        byte[] badPeOff = new byte[0x200];
        badPeOff[0] = (byte)'M';
        badPeOff[1] = (byte)'Z';
        BitConverter.GetBytes(-1).CopyTo(badPeOff, 0x3C);
        Assert.Null(SkillCensus.PeImage.TryParse(badPeOff));

        BitConverter.GetBytes(0x1C0).CopyTo(badPeOff, 0x3C); // peOff + 24 + 96 > 0x200
        Assert.Null(SkillCensus.PeImage.TryParse(badPeOff));

        byte[] badPeSig = new byte[0x200];
        badPeSig[0] = (byte)'M';
        badPeSig[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(badPeSig, 0x3C);
        Assert.Null(SkillCensus.PeImage.TryParse(badPeSig));

        byte[] smallOpt = new byte[0x300];
        smallOpt[0] = (byte)'M'; smallOpt[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(smallOpt, 0x3C);
        smallOpt[0x80] = (byte)'P'; smallOpt[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)1).CopyTo(smallOpt, 0x80 + 6);
        BitConverter.GetBytes((ushort)31).CopyTo(smallOpt, 0x80 + 20);
        Assert.Null(SkillCensus.PeImage.TryParse(smallOpt));

        var sb = new SyntheticPeBuilder();
        byte[] raw = sb.Build(sectionCount: 4);
        SkillCensus.PeImage pe = SkillCensus.PeImage.TryParse(raw);
        Assert.NotNull(pe);

        // #0(.text) 边界
        Assert.False(pe!.IsTextVa(sb.TextVa(0) - 1));
        Assert.True(pe.IsTextVa(sb.TextVa(0)));
        Assert.True(pe.IsTextVa(sb.TextVa(sb.Text.Length - 1)));
        Assert.False(pe.IsTextVa(sb.TextVa(sb.Text.Length)));

        // #1(.rdata) 与 #2(.data) 边界
        Assert.False(pe.IsRdataVa(sb.RdataVa(0) - 1));
        Assert.True(pe.IsRdataVa(sb.RdataVa(0)));
        Assert.True(pe.IsRdataVa(sb.RdataVa(sb.Rdata.Length - 1)));
        Assert.False(pe.IsRdataVa(sb.RdataVa(sb.Rdata.Length)));

        Assert.True(pe.IsStaticDataVa(sb.RdataVa(0)));
        Assert.True(pe.IsStaticDataVa(sb.DataVa(sb.Data.Length - 1)));
        Assert.False(pe.IsStaticDataVa(sb.TextVa(0)));

        // 第 4 节(.prot) 被严格忽略: 既不算静态数据, 也不映射到偏移
        Assert.False(pe.IsStaticDataVa(sb.ProtVa(0)));
        Assert.False(pe.TryVaToOff(sb.ProtVa(0), out _));

        // TryReadU32 边界
        sb.WriteU32(sb.Data, sb.Data.Length - 4, 0x12345678u);
        pe = SkillCensus.PeImage.TryParse(sb.Build(sectionCount: 3));
        Assert.NotNull(pe);
        Assert.True(pe!.TryReadU32(sb.DataVa(sb.Data.Length - 4), out uint u32));
        Assert.Equal(0x12345678u, u32);
        Assert.False(pe.TryReadU32(sb.DataVa(sb.Data.Length - 3), out _));

        // 截断的节表/越界节指针应被拒绝
        byte[] trunc = sb.Build(sectionCount: 3);
        Array.Resize(ref trunc, trunc.Length - 1);
        Assert.Null(SkillCensus.PeImage.TryParse(trunc));
    }

    [Fact]
    public void TryDecodeX86_CoversInstructionsPrefixesAndBounds()
    {
        // 空/越界返回 false
        Assert.False(SkillCensus.TryDecodeX86(Array.Empty<byte>(), 0, 0, 0x401000, out _));
        Assert.False(SkillCensus.TryDecodeX86(new byte[] { 0x90 }, -1, 1, 0x401000, out _));
        Assert.False(SkillCensus.TryDecodeX86(new byte[] { 0x66 }, 0, 1, 0x401000, out _)); // 仅前缀截断

        // mov edx, [eax+0x30c] (ReadsMemDisp 命中与不命中)
        byte[] movDisp = { 0x8B, 0x90, 0x0C, 0x03, 0x00, 0x00 };
        Assert.True(SkillCensus.TryDecodeX86(movDisp, 0, movDisp.Length, 0x401000, out var ins));
        Assert.Equal(SkillCensus.X86OpKind.Mov, ins.Kind);
        Assert.Equal(6, ins.Length);
        Assert.True(ins.ReadsMemDisp(0x30C));
        Assert.False(ins.ReadsMemDisp(0x308));

        // mod=3 寄存器-寄存器 mov 不算 ReadsMemDisp
        byte[] movReg = { 0x8B, 0xD0 };
        Assert.True(SkillCensus.TryDecodeX86(movReg, 0, movReg.Length, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Mov, ins.Kind);
        Assert.False(ins.ReadsMemDisp(0));

        // 0F 84 rel32 (Jcc) 与 66 0F 84 rel16
        byte[] jcc32 = { 0x0F, 0x84, 0x10, 0x00, 0x00, 0x00 };
        Assert.True(SkillCensus.TryDecodeX86(jcc32, 0, jcc32.Length, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Jcc, ins.Kind);
        Assert.True(ins.HasRelTarget);
        Assert.Equal(0x401016u, ins.RelTarget);

        // 0F 31 (rdtsc 无 ModRM) 与 0F 70 imm8
        byte[] rdtsc = { 0x0F, 0x31 };
        Assert.True(SkillCensus.TryDecodeX86(rdtsc, 0, rdtsc.Length, 0x401000, out ins));
        Assert.Equal(2, ins.Length);
        byte[] pshufw = { 0x0F, 0x70, 0xC1, 0x4E };
        Assert.True(SkillCensus.TryDecodeX86(pshufw, 0, pshufw.Length, 0x401000, out ins));
        Assert.Equal(4, ins.Length);
        Assert.Equal(0x4E, ins.Imm);

        // SIB base=5 disp32: 8B 04 25 10 00 00 00 (mod=0 rm=4 sib=0x25 -> base=5 disp32)
        byte[] sibDisp = { 0x8B, 0x04, 0x25, 0x10, 0x00, 0x00, 0x00 };
        Assert.True(SkillCensus.TryDecodeX86(sibDisp, 0, sibDisp.Length, 0x401000, out ins));
        Assert.Equal(7, ins.Length);
        Assert.True(ins.HasSib);
        Assert.Equal(5, ins.SibBase);
        Assert.Equal(0x10, ins.Disp);

        // addr16 mod=0 rm=6 disp16: 67 8B 06 34 12
        byte[] addr16 = { 0x67, 0x8B, 0x06, 0x34, 0x12 };
        Assert.True(SkillCensus.TryDecodeX86(addr16, 0, addr16.Length, 0x401000, out ins));
        Assert.Equal(5, ins.Length);
        Assert.Equal(0x1234, ins.Disp);

        // F6/0 test imm8 vs F6/2 not (无 imm)
        byte[] testImm = { 0xF6, 0xC0, 0x04 };
        Assert.True(SkillCensus.TryDecodeX86(testImm, 0, testImm.Length, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Test, ins.Kind);
        Assert.Equal(3, ins.Length);
        Assert.Equal(4, ins.Imm);

        byte[] notOp = { 0xF6, 0xD0 };
        Assert.True(SkillCensus.TryDecodeX86(notOp, 0, notOp.Length, 0x401000, out ins));
        Assert.Equal(2, ins.Length);
        Assert.False(ins.HasImm);

        // 83/0 add, 83/5 sub, 83/7 cmp
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x83, 0xC4, 0x04 }, 0, 3, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Add, ins.Kind);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x83, 0xE8, 0x01 }, 0, 3, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Sub, ins.Kind);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x83, 0xF8, 0xFF }, 0, 3, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Cmp, ins.Kind);
        Assert.Equal(-1, ins.Imm);

        // FF/2 call, FF/4 jmp, FF/6 push
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xFF, 0xD2 }, 0, 2, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Call, ins.Kind);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xFF, 0xE0 }, 0, 2, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Jmp, ins.Kind);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xFF, 0xF1 }, 0, 2, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Push, ins.Kind);

        // 0x66 0x0F 0x84 rel16 (Jcc op16) 与 0x0F 0x3A 4-byte op
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x66, 0x0F, 0x84, 0x10, 0x00 }, 0, 5, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Jcc, ins.Kind);
        Assert.Equal(5, ins.Length);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x0F, 0x3A, 0x0F, 0xC1, 0x01 }, 0, 5, 0x401000, out ins));
        Assert.Equal(5, ins.Length);

        // ret imm16 / enter / far call / moffs / xor / cmp al / F7 test / jmp rel8
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xC2, 0x08, 0x00 }, 0, 3, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Ret, ins.Kind);
        Assert.Equal(8, ins.Imm);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xC8, 0x10, 0x00, 0x00 }, 0, 4, 0x401000, out ins));
        Assert.Equal(4, ins.Length);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x9A, 0x01, 0x02, 0x03, 0x04, 0x05, 0x00 }, 0, 7, 0x401000, out ins));
        Assert.Equal(7, ins.Length);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xA1, 0x40, 0x30, 0x20, 0x00 }, 0, 5, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Mov, ins.Kind);
        Assert.Equal(0x00203040, ins.Disp);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x67, 0xA1, 0x20, 0x10 }, 0, 4, 0x401000, out ins));
        Assert.Equal(4, ins.Length);
        Assert.Equal(0x1020, ins.Disp);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x33, 0xC0 }, 0, 2, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Xor, ins.Kind);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0x3C, 0x05 }, 0, 2, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Cmp, ins.Kind);
        Assert.Equal(5, ins.Imm);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xF7, 0xC0, 0x02, 0x00, 0x00, 0x00 }, 0, 6, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Test, ins.Kind);
        Assert.Equal(2, ins.Imm);
        Assert.True(SkillCensus.TryDecodeX86(new byte[] { 0xEB, 0xFE }, 0, 2, 0x401000, out ins));
        Assert.Equal(SkillCensus.X86OpKind.Jmp, ins.Kind);
        Assert.Equal(0x401000u, ins.RelTarget);
    }

    [Fact]
    public void Helpers_CoverageLabelsPriorityAndCastability_MatchExactBoundaries()
    {
        Assert.Equal("0x00", SkillCensus.FormatHex(0));
        Assert.Equal("0x0d", SkillCensus.FormatHex(13));
        Assert.Equal("0x3f", SkillCensus.FormatHex(63));

        Assert.Equal("否", SkillCensus.DescribeStateRecipeCoverage(0, 5));
        Assert.Equal("部分(1/5)", SkillCensus.DescribeStateRecipeCoverage(1, 5));
        Assert.Equal("部分(4/5)", SkillCensus.DescribeStateRecipeCoverage(4, 5));
        Assert.Equal("是(全覆盖)", SkillCensus.DescribeStateRecipeCoverage(5, 5));
        Assert.Equal("是(全覆盖)", SkillCensus.DescribeStateRecipeCoverage(6, 5));

        // 优先级: Squirrel < Dispatch < LoopTable < BaseBuff < PerStatePointer < KeyHub
        Assert.Equal(SkillCensus.EntryKind.Squirrel, SkillCensus.ChoosePrimaryKind(new List<SkillCensus.EntryPoint>
        {
            new(0x11, SkillCensus.EntryKind.KeyHub),
            new(0x2A, SkillCensus.EntryKind.Squirrel),
            new(0x0D, SkillCensus.EntryKind.LoopTable),
        }));
        Assert.Equal(SkillCensus.EntryKind.Dispatch, SkillCensus.ChoosePrimaryKind(new List<SkillCensus.EntryPoint>
        {
            new(0x0D, SkillCensus.EntryKind.KeyHub),
            new(0x2A, SkillCensus.EntryKind.Dispatch),
        }));
        Assert.Equal(SkillCensus.EntryKind.BaseBuff, SkillCensus.ChoosePrimaryKind(new List<SkillCensus.EntryPoint>
        {
            new(0x11, SkillCensus.EntryKind.PerStatePointer),
            new(0x11, SkillCensus.EntryKind.BaseBuff),
        }));

        // 跨职业共享口径: 首职业有专属技能(如 1, 24), 非首职业仅通用后跳/倒地起身/基类 Buff(如 175)
        Assert.True(SkillCensus.IsCastableForJob(0, 1));
        Assert.False(SkillCensus.IsCastableForJob(1, 1));
        Assert.True(SkillCensus.IsCastableForJob(1, SkillIds.BackStep));
        Assert.True(SkillCensus.IsCastableForJob(1, SkillIds.QuickStand));
        Assert.True(SkillCensus.IsCastableForJob(1, 175));
        Assert.False(SkillCensus.IsCastableForJob(1, 24));

        Assert.True(SkillCensus.TryParseIntLiteral("0x1a", out int hexVal));
        Assert.Equal(26, hexVal);
        Assert.True(SkillCensus.TryParseIntLiteral("-0x1a", out int negHexVal));
        Assert.Equal(-26, negHexVal);
        Assert.True(SkillCensus.TryParseIntLiteral("-42", out int decVal));
        Assert.Equal(-42, decVal);
        Assert.False(SkillCensus.TryParseIntLiteral("not_num", out _));
    }

    [Fact]
    public void DiscoverKeyHelperVa_RequiresAtLeast100Sites()
    {
        var sb99 = new SyntheticPeBuilder();
        uint targetVa = sb99.TextVa(0x100);
        sb99.EmitRet(0x100);
        for (int k = 0; k < 99; k++)
        {
            int o = 0x400 + k * 36;
            sb99.Text[o] = 0x8B; sb99.Text[o + 1] = 0x90;
            BitConverter.GetBytes(0x918u).CopyTo(sb99.Text, o + 2);
            sb99.EmitCallRel32(o + 6, targetVa);
        }
        var pe99 = SkillCensus.PeImage.TryParse(sb99.Build(sectionCount: 3))!;
        Assert.Equal(0u, SkillCensus.DiscoverKeyHelperVa(pe99));

        var sb100 = new SyntheticPeBuilder();
        targetVa = sb100.TextVa(0x100);
        sb100.EmitRet(0x100);
        for (int k = 0; k < 100; k++)
        {
            int o = 0x400 + k * 36;
            sb100.Text[o] = 0x8B; sb100.Text[o + 1] = 0x90;
            BitConverter.GetBytes(0x918u).CopyTo(sb100.Text, o + 2);
            sb100.EmitCallRel32(o + 6, targetVa);
        }
        var pe100 = SkillCensus.PeImage.TryParse(sb100.Build(sectionCount: 3))!;
        Assert.Equal(targetVa, SkillCensus.DiscoverKeyHelperVa(pe100));
    }

    [Fact]
    public void WriteReport_MissingCharacterList_ReturnsErrorLine()
    {
        (byte[] emptyPvf, _) = TestData.BuildPvf(Array.Empty<TestData.PvfFileSpec>(), validChecksums: true);
        using var pvf = new PvfArchive(new MemoryStream(emptyPvf));
        var sw = new StringWriter();

        int code = SkillCensus.WriteReport(pvf, pe: null, sw);

        Assert.Equal(1, code);
        Assert.Equal(new[] { "  [失败] 读不到角色表" }, sw.ToString().TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));
    }

    [Fact]
    public void WriteReport_SyntheticPvfAndPe_ExactSanitizedLinesAndAllScopes()
    {
        (PvfArchive pvf, byte[] peBytes) = BuildSyntheticWorld();
        using (pvf)
        {
            SkillCensus.PeImage pe = SkillCensus.PeImage.TryParse(peBytes)!;
            var sw = new StringWriter();
            bool SyntheticCastable(int job, int skill) => (job == 10 && skill == 904) || (job == 20 && (skill == 911 || skill == 921)) || (job == 30 && skill == 921);

            int code = SkillCensus.WriteReport(pvf, pe, sw, SyntheticCastable);
            Assert.Equal(0, code);

            string output = sw.ToString();
            string[] lines = output.TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // 脱敏逐字检查: 任何真实/合成职业名、技能名、脚本路径都不能出现在输出中
            Assert.DoesNotContain("alpha", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("beta", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gamma", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("delta", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Secret", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".chr", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".skl", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".nut", output, StringComparison.OrdinalIgnoreCase);

            // 逐行手算字面期望断言
            var expected = new[]
            {
                "技能普查: 角色脚本共 4 个, 客户端二进制=已读入普通节",
                "",
                "一、分职业技能与起手路径汇总(严格匿名编号)",
                "职业   | 总技能 | 主动 | 被动 | 有指令主动 | 已有配方已能施放 | 起手匹配数(Squirrel/分派/循环表/基类/指针键/过程键/未匹配) | 可读原生状态数 | 原生状态集合",
                new string('-', 128),
                "职业甲  |      6 |    5 |    1 |          4 |                1 | 0/1/1/1/0/1/1                                 |              3 | 0x0d,0x11,0x2a",
                "职业乙  |      6 |    6 |    0 |          6 |                2 | 0/0/3/0/0/3/0                                 |              4 | 0x0d,0x11,0x2a,0x32",
                "职业丙  |      3 |    2 |    1 |          2 |                1 | 1/0/0/0/0/1/0                                 |              3 | 0x2a,0x32,0x35",
                "职业丁  |      2 |    2 |    0 |          2 |                0 | 1/0/0/0/1/0/0                                 |              2 | 0x31,0x36",
                "",
                "全职业主动技能已有配方已能施放合计: 4 (按职业作用域与基类共享表统计)",
                "",
                "二、跨职业原生状态号直方图(按可解锁主动技能数从大到小排序)",
                "状态号 | 涉及职业数 | 关联主动技能数 | 作用域     | 已有配方状态 | 已实现关联技能数 | 涉及职业",
                new string('-', 104),
                "0x11   |          2 |              4 | 基类共享(同码)         | 部分(1/4)        |                1 | 职业甲,职业乙",
                "0x2a   |          3 |              3 | 异类同号(各自状态)       | 否              |                0 | 职业甲,职业乙,职业丙",
                "0x0d   |          2 |              3 | 基类共享(同码)         | 部分(1/3)        |                1 | 职业甲,职业乙",
                "0x32   |          2 |              2 | 同C++类共享          | 是(全覆盖)         |                2 | 职业乙,职业丙",
                "0x31   |          1 |              1 | 单C++类局部          | 否              |                0 | 职业丁",
                "0x36   |          1 |              1 | 单C++类局部          | 否              |                0 | 职业丁",
            };

            Assert.Equal(expected, lines);
        }
    }

    [Fact]
    public void WriteReport_WithoutPe_StillExtractsSquirrelAndMarksBinaryMissing()
    {
        (PvfArchive pvf, _) = BuildSyntheticWorld();
        using (pvf)
        {
            var sw = new StringWriter();
            int code = SkillCensus.WriteReport(pvf, pe: null, sw, (_, _) => false);
            Assert.Equal(0, code);

            string[] lines = sw.ToString().TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            Assert.Equal("技能普查: 角色脚本共 4 个, 客户端二进制=缺失(仅统计脚本侧)", lines[0]);
            Assert.Equal("职业甲  |      6 |    5 |    1 |          4 |                0 | 0/0/0/0/0/0/5                                 |              0 | -", lines[5]);
            Assert.Equal("职业丙  |      3 |    2 |    1 |          2 |                0 | 1/0/0/0/0/0/1                                 |              2 | 0x2a,0x35", lines[7]);
            Assert.Equal("0x2a   |          1 |              1 | 单C++类局部          | 否              |                0 | 职业丙", lines[15]);
        }
    }

    [Fact]
    public void WriteReport_IndexLoopAndDescentAndOverflowJobLabels_WorkAsExpected()
    {
        // 验证下标步进循环表(!isPtrLoop) + TraceSetStates 浅下潜 1 层 + 超过 11 职业时的 "职业12" 标签 + 注释与负十六进制常量忽略
        var pe = new SyntheticPeBuilder();
        uint keyGetterVa = pe.TextVa(0x100); pe.EmitRet(0x100);
        uint setStateVa = pe.TextVa(0x120); pe.EmitRet(0x120);
        uint keyHelperVa = pe.TextVa(0x140); pe.EmitRet(0x140);

        var vtables = new uint[11];
        for (int i = 0; i < 11; i++)
        {
            int vtOff = i * 0x500;
            vtables[i] = pe.RdataVa(vtOff);
            int procOff = 0x10000 + i * 0x18000;
            int enterOff = procOff + 0x200;
            pe.EmitRet(procOff);
            pe.EmitRet(enterOff);
            pe.WriteU32(pe.Rdata, vtOff + 0x1F0, pe.TextVa(procOff));
            pe.WriteU32(pe.Rdata, vtOff + 0x29C, pe.TextVa(enterOff));
            pe.WriteU32(pe.Rdata, vtOff + 0x30C, setStateVa);
            pe.WriteU32(pe.Rdata, vtOff + 0x918, keyGetterVa);
        }

        int dummyBaseOff = 0x120000;
        for (int k = 0; k < 100; k++)
        {
            int o = dummyBaseOff + k * 36;
            pe.Text[o] = 0x8B; pe.Text[o + 1] = 0x90;
            BitConverter.GetBytes(0x918u).CopyTo(pe.Text, o + 2);
            pe.EmitCallRel32(o + 6, keyHelperVa);
        }

        // 2 个职业的构造与跳转表(让 job 0..11 共 12 个职业都能触发分析)
        int ctor1 = 0x110000;
        int pC = pe.EmitMovEaxImm(ctor1, vtables[1]);
        pe.EmitRet(pC);
        int sw = 0x110200;
        int tbl = 0x110300;
        for (int j = 0; j < 12; j++)
        {
            int ce = sw + j * 0x10;
            int r = pe.EmitCallRel32(ce, pe.TextVa(ctor1));
            pe.EmitRet(r);
            pe.WriteU32(pe.Text, tbl + j * 4, pe.TextVa(ce));
        }

        // Class 1 (proc=0x28000): 1) 下标循环表 (.data [931, 932, 933] 但 cmp esi, 2 截断为 2 项) + 浅下潜辅助函数 -> 0x37
        pe.WriteU32(pe.Data, 0x40, 931u);
        pe.WriteU32(pe.Data, 0x44, 932u);
        pe.WriteU32(pe.Data, 0x48, 933u);
        pe.WriteU32(pe.Data, 0x4C, 0u);
        int helperOff = 0x28400;
        int h = pe.EmitSetState(helperOff, 0x37);
        pe.EmitRet(h);

        int idxSite = 0x28060;
        // mov ecx, [esi*4 + DataVa(0x40)] => 8B 0C B5 <disp32>
        pe.Text[idxSite++] = 0x8B; pe.Text[idxSite++] = 0x0C; pe.Text[idxSite++] = 0xB5;
        BitConverter.GetBytes(pe.DataVa(0x40)).CopyTo(pe.Text, idxSite);
        idxSite += 4;
        pe.Text[idxSite++] = 0x51; // push ecx
        idxSite = pe.EmitCallRel32(idxSite, keyHelperVa);
        pe.Text[idxSite++] = 0x83; pe.Text[idxSite++] = 0xF8; pe.Text[idxSite++] = 0xFF;
        int idxJe = idxSite; idxSite += 2;
        idxSite = pe.EmitCallRel32(idxSite, pe.TextVa(helperOff));
        pe.EmitJeRel8(idxJe, idxSite);
        // cmp esi, 2 => 83 FE 02 (curVa > callVa 且 imm==2)
        pe.Text[idxSite++] = 0x83; pe.Text[idxSite++] = 0xFE; pe.Text[idxSite++] = 0x02;
        pe.EmitRet(idxSite);

        var pool = new List<string>();
        byte[] BuildList(params (int Key, string Val)[] rows)
        {
            var tb = new TokenStreamBuilder(pool);
            foreach (var (k, v) in rows) tb.Int(k).Str(v);
            return tb.Bytes();
        }
        byte[] MakeSkill(bool active = true)
        {
            var tb = new TokenStreamBuilder(pool);
            tb.Tag("[type]").Str(active ? "[active]" : "[passive]");
            if (active) tb.Tag("[command]").Str("X");
            return tb.Bytes();
        }
        var charRows = Enumerable.Range(1, 12).Select(i => (i, $"c{i}/c{i}.chr")).ToArray();
        var skListRows = Enumerable.Range(1, 12).Select(i => (i, i == 12 ? "s12.lst" : i == 11 ? "s11.lst" : "s_empty.lst")).ToArray();
        var files = new List<TestData.PvfFileSpec>
        {
            new("character/character.lst", BuildList(charRows), 0),
            new("skill/skilllist.lst", BuildList(skListRows), 0),
            new("skill/s_empty.lst", BuildList(), 0),
            new("skill/s11.lst", BuildList((934, "s934.skl")), 0),
            new("skill/s12.lst", BuildList((931, "s931.skl"), (932, "s932.skl"), (933, "s933.skl")), 0),
            new("skill/s931.skl", MakeSkill(true), 0),
            new("skill/s932.skl", MakeSkill(true), 0),
            new("skill/s933.skl", MakeSkill(true), 0),
            new("skill/s934.skl", MakeSkill(false), 0),
            new("sqr/loadstate.nut", Encoding.UTF8.GetBytes("sq_RunScript(\"sq12.nut\");\n"), 0),
            new("sqr/hdr12.nut", Encoding.UTF8.GetBytes(
                "// COMMENTED_STATE <- 0x39\n" +
                "local NEG_STATE <- -0x10;\n" +
                "const ZERO_STATE = 0x00;\n" +
                "const GOOD_STATE = 0x38;\n"), 0),
            new("sqr/sq12.nut", Encoding.UTF8.GetBytes(
                "pushScriptFiles(\"hdr12.nut\");\n" +
                "IRDSQRCharacter.pushState(11, \"x.nut\", \"X\", ZERO_STATE, 934);\n" +
                "IRDSQRCharacter.pushState(12, \"x.nut\", \"X\", NEG_STATE, 931);\n" +
                "IRDSQRCharacter.pushState(12, \"x.nut\", \"X\", COMMENTED_STATE, 931);\n" +
                "IRDSQRCharacter.pushState(12, \"x.nut\", \"X\", ZERO_STATE, 932);\n" +
                "IRDSQRCharacter.pushState(12, \"x.nut\", \"X\", GOOD_STATE, 932);\n"), 0),
        };
        files.Insert(0, new TestData.PvfFileSpec("stringtable.bin", new TokenStreamBuilder(pool).StringTableBytes(), 0));
        (byte[] pvfBytes, _) = TestData.BuildPvf(files, validChecksums: true);

        using var pvf = new PvfArchive(new MemoryStream(pvfBytes));
        var parsedPe = SkillCensus.PeImage.TryParse(pe.Build(sectionCount: 3))!;
        var writer = new StringWriter();
        Assert.Equal(0, SkillCensus.WriteReport(pvf, parsedPe, writer, (_, _) => false));

        string[] lines = writer.ToString().TrimEnd('\r', '\n').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        Assert.Equal("技能普查: 角色脚本共 12 个, 客户端二进制=已读入普通节", lines[0]);
        Assert.Equal("职业子  |      1 |    0 |    1 |          0 |                0 | 0/0/0/0/0/0/0                                 |              1 | 0x00", lines[15]);
        Assert.Equal("职业12 |      3 |    3 |    0 |          3 |                0 | 1/0/1/0/0/0/1                                 |              3 | 0x00,0x37,0x38", lines[16]);
        Assert.Equal("0x37   |          1 |              2 | 单C++类局部          | 否              |                0 | 职业12", lines[23]);
        Assert.Equal("0x00   |          1 |              1 | 基类共享(同码)         | 否              |                0 | 职业12", lines[24]);
        Assert.Equal("0x38   |          1 |              1 | 单C++类局部          | 否              |                0 | 职业12", lines[25]);
    }

    [Fact]
    public void DescribeStateRecipeCoverageAndPeMzValidationHandleZeroAndCorruptedHeaders()
    {
        Assert.Equal("否", SkillCensus.DescribeStateRecipeCoverage(0, 0));
        Assert.Equal("否", SkillCensus.DescribeStateRecipeCoverage(0, 5));
        Assert.Equal("部分(2/5)", SkillCensus.DescribeStateRecipeCoverage(2, 5));
        Assert.Equal("是(全覆盖)", SkillCensus.DescribeStateRecipeCoverage(5, 5));

        var validPe = new SyntheticPeBuilder().Build(sectionCount: 3);
        Assert.NotNull(SkillCensus.PeImage.TryParse(validPe));

        var badM = (byte[])validPe.Clone();
        badM[0] = (byte)'X';
        Assert.Null(SkillCensus.PeImage.TryParse(badM));

        var badZ = (byte[])validPe.Clone();
        badZ[1] = (byte)'Y';
        Assert.Null(SkillCensus.PeImage.TryParse(badZ));

        // 阶段 2a 职业作用域接入校验: 跨职业配方与共享基类项生效,首职业局部项不泄漏给其它职业
        Assert.True(SkillCensus.IsCastableForJob(9, 1));
        Assert.False(SkillCensus.IsCastableForJob(2, 1));
        Assert.True(SkillCensus.IsCastableForJob(2, SkillIds.BackStep));
        Assert.False(SkillCensus.IsCastableForJob(-1, SkillIds.BackStep));
    }

    // ------------------------------------------------------------------ 变异测试补缺口(r19 修补一)
    // 下面 7 组用例补的是集成者变异测试里"现有测试全绿也杀不死"的缺口:普通节访问器的节数边界、
    // 逐状态指针写入的 C7 三种 ModRM 写法、无置状态调用时的 -1 哨兵、技能号 1 的下界、add esp 清栈的判断。
    // 全部用合成映像与合成编号,期望值手算,不依赖墙钟与执行顺序。

    /// <summary>骨架里第 <paramref name="cls"/> 个类的过程入口在 .text 里的偏移(类与类相隔 0x18000,进入入口在其后 0x200)。</summary>
    private static int ClassProcOff(int cls) => 0x10000 + cls * 0x18000;

    /// <summary>
    /// 最小的"能被 <see cref="SkillCensus.Analyze"/> 识别"的合成映像骨架:11 张 Character 虚表(.rdata 里相隔 0x500,
    /// 共享 +0x918 按键读取槽与 +0x30c 切状态槽,+0x1f0/+0x29c 各不相同)、100 处 `mov edx,[eax+0x918]; call keyHelper`
    /// (凑够按键助手的发现门限)、其余 .text 全是 0xCC。只放骨架,不放任何技能起手点。
    /// </summary>
    private static (SyntheticPeBuilder Pe, uint[] Vtables, uint KeyHelperVa) BuildClassSkeleton()
    {
        var pe = new SyntheticPeBuilder();
        uint keyGetterVa = pe.TextVa(0x100);
        pe.EmitRet(0x100);
        uint setStateVa = pe.TextVa(0x120);
        pe.EmitRet(0x120);
        uint keyHelperVa = pe.TextVa(0x140);
        pe.EmitRet(0x140);

        var vtables = new uint[11];
        for (int i = 0; i < 11; i++)
        {
            int vtOff = i * 0x500;
            int procOff = ClassProcOff(i);
            vtables[i] = pe.RdataVa(vtOff);
            pe.EmitRet(procOff);
            pe.EmitRet(procOff + 0x200);
            pe.WriteU32(pe.Rdata, vtOff + 0x1F0, pe.TextVa(procOff));
            pe.WriteU32(pe.Rdata, vtOff + 0x29C, pe.TextVa(procOff + 0x200));
            pe.WriteU32(pe.Rdata, vtOff + 0x30C, setStateVa);
            pe.WriteU32(pe.Rdata, vtOff + 0x918, keyGetterVa);
        }

        for (int k = 0; k < 100; k++)
        {
            int o = 0x120000 + k * 36;
            pe.Text[o] = 0x8B;
            pe.Text[o + 1] = 0x90;
            BitConverter.GetBytes(0x918u).CopyTo(pe.Text, o + 2);
            pe.EmitCallRel32(o + 6, keyHelperVa);
        }
        return (pe, vtables, keyHelperVa);
    }

    /// <summary>
    /// 一个按键助手起手调用点:`prefix; call keyHelper; cmp eax,-1; je skip; body; skip: ret`
    /// (与 BuildSyntheticWorld 里的 EmitKeyHubSite 同形,但前导与 `je` 之后的内容由调用方决定)。返回 ret 之后的偏移。
    /// </summary>
    private static int EmitHubSite(SyntheticPeBuilder pe, uint keyHelperVa, int off, Func<int, int> prefix, Func<int, int> body)
    {
        int q = prefix(off);
        q = pe.EmitCallRel32(q, keyHelperVa);
        pe.Text[q++] = 0x83; pe.Text[q++] = 0xF8; pe.Text[q++] = 0xFF; // cmp eax, -1
        int je = q;
        q += 2;
        q = body(q);
        pe.EmitJeRel8(je, q);
        return pe.EmitRet(q);
    }

    /// <summary>对合成映像直接跑原生分析(空脚本库、只传 1 个职业:跳过与本组用例无关的工厂职业继承推断)。</summary>
    private static SkillCensus.NativeAnalysis AnalyzeSkeleton(SyntheticPeBuilder pe)
    {
        (byte[] emptyPvf, _) = TestData.BuildPvf(Array.Empty<TestData.PvfFileSpec>(), validChecksums: true);
        using var pvf = new PvfArchive(new MemoryStream(emptyPvf));
        SkillCensus.PeImage image = SkillCensus.PeImage.TryParse(pe.Build(sectionCount: 3));
        Assert.NotNull(image);
        return SkillCensus.Analyze(pvf, image, new[] { 0 });
    }

    private static List<SkillCensus.EntryPoint> EntriesOf(SkillCensus.NativeAnalysis analysis, uint vtable, int skillId)
    {
        Assert.True(analysis.ClassEntries.TryGetValue(vtable, out Dictionary<int, List<SkillCensus.EntryPoint>> bySkill), "该类没有登记任何起手点");
        Assert.True(bySkill.TryGetValue(skillId, out List<SkillCensus.EntryPoint> entries), "该类没有登记这个技能号");
        return entries;
    }

    /// <summary>在类 1 的过程区放一个以 `push skillId` 起头的起手点,<paramref name="body"/> 决定 `je` 之后的内容,返回该技能号登记的入口点。</summary>
    private static List<SkillCensus.EntryPoint> EntriesOfSingleSite(int skillId, Func<SyntheticPeBuilder, int, int> body)
    {
        (SyntheticPeBuilder pe, uint[] vtables, uint keyHelperVa) = BuildClassSkeleton();
        EmitHubSite(pe, keyHelperVa, ClassProcOff(1) + 0x60, q => pe.EmitPushImm(q, skillId), q => body(pe, q));
        return EntriesOf(AnalyzeSkeleton(pe), vtables[1], skillId);
    }

    [Fact]
    public void PeImage_ZeroAndSingleOrdinarySection_OnlyTextWindowIsExposedAndRdataAccessorsStayZero()
    {
        // 手算(合成映像):ImageBase=0x00400000;.text:RVA 0x1000、原始大小 0x140000、原始偏移=节头区对齐后的 0x200。
        // 节表起于 0x80+24+0xE0=0x178,0/1 个节时 0x178+40n ≤ 0x200,节头区按 0x200 对齐后恰为 0x200。
        var sb = new SyntheticPeBuilder();

        // 0 个节:TryParse 给出空节表映像,所有节访问器与按 VA 的判定都是 0 / false,且不抛异常
        SkillCensus.PeImage none = SkillCensus.PeImage.TryParse(sb.Build(sectionCount: 0));
        Assert.NotNull(none);
        Assert.Equal((0u, 0u, 0u, 0), (none.TextStartVa, none.TextEndVa, none.TextRawOffset, none.TextRawSize));
        Assert.Equal((0u, 0u, 0u, 0), (none.RdataStartVa, none.RdataEndVa, none.RdataRawOffset, none.RdataRawSize));
        Assert.False(none.IsTextVa(sb.TextVa(0)));
        Assert.False(none.IsRdataVa(sb.RdataVa(0)));
        Assert.False(none.IsStaticDataVa(sb.RdataVa(0)));
        Assert.False(none.TryVaToOff(sb.TextVa(0), out _));

        // 只有 1 个普通节(.text):TextStartVa = ImageBase + TextRva = 0x00401000;
        // TextEndVa = 0x00401000 + 0x140000 = 0x00541000;原始偏移 0x200;原始大小 0x140000。
        SkillCensus.PeImage one = SkillCensus.PeImage.TryParse(sb.Build(sectionCount: 1));
        Assert.NotNull(one);
        Assert.Equal(0x00401000u, SyntheticPeBuilder.ImageBase + SyntheticPeBuilder.TextRva);
        Assert.Equal(0x00401000u, one.TextStartVa);
        Assert.Equal(0x00541000u, one.TextEndVa);
        Assert.Equal(0x200u, one.TextRawOffset);
        Assert.Equal(0x140000, one.TextRawSize);

        // 没有第二节:.rdata 的四个访问器全为 0(尤其 RdataRawOffset 不能越界取 [1] 抛异常)
        Assert.Equal(0u, one.RdataStartVa);
        Assert.Equal(0u, one.RdataEndVa);
        Assert.Equal(0u, one.RdataRawOffset);
        Assert.Equal(0, one.RdataRawSize);

        // 依赖这些访问器的按 VA 判定也跟着自洽:.text 边界内外、.rdata/.data 一律不认、.text 内偏移 = 0x200 + 0x10
        Assert.False(one.IsTextVa(0x00401000u - 1));
        Assert.True(one.IsTextVa(0x00401000u));
        Assert.True(one.IsTextVa(0x00541000u - 1));
        Assert.False(one.IsTextVa(0x00541000u));
        Assert.False(one.IsRdataVa(sb.RdataVa(0)));
        Assert.False(one.IsStaticDataVa(sb.RdataVa(0)));
        Assert.False(one.TryVaToOff(sb.RdataVa(0), out _));
        Assert.True(one.TryVaToOff(0x00401010u, out int textOff));
        Assert.Equal(0x210, textOff);
    }

    [Fact]
    public void PeImage_TwoThreeAndFourSections_ExposeHandComputedTextAndRdataWindows()
    {
        // 手算:节表起于 0x80+24+0xE0=0x178,每节 40 字节,节头区按 0x200 对齐后才是 .text 的原始偏移:
        //   2 节 0x178+80=0x1C8 → 0x200;3 节 0x178+120=0x1F0 → 0x200;4 节 0x178+160=0x218 → 0x400。
        // .text 原始大小 0x140000,所以 .rdata 原始偏移 = .text 原始偏移 + 0x140000(0x140200 / 0x140200 / 0x140400)。
        // .text:RVA 0x1000 → VA 0x00401000..0x00541000;.rdata:RVA 0x200000、大小 0x8000 → VA 0x00600000..0x00608000。
        // 第 3 节(.data)与第 4 节(.prot)不进这两组访问器。
        var sb = new SyntheticPeBuilder();
        foreach ((int sections, uint textRaw, uint rdataRaw) in new[] { (2, 0x200u, 0x140200u), (3, 0x200u, 0x140200u), (4, 0x400u, 0x140400u) })
        {
            SkillCensus.PeImage pe = SkillCensus.PeImage.TryParse(sb.Build(sectionCount: sections));
            Assert.NotNull(pe);
            Assert.Equal((0x00401000u, 0x00541000u, textRaw, 0x140000), (pe.TextStartVa, pe.TextEndVa, pe.TextRawOffset, pe.TextRawSize));
            Assert.Equal((0x00600000u, 0x00608000u, rdataRaw, 0x8000), (pe.RdataStartVa, pe.RdataEndVa, pe.RdataRawOffset, pe.RdataRawSize));
            Assert.Equal(0x00600000u, SyntheticPeBuilder.ImageBase + SyntheticPeBuilder.RdataRva);
        }
    }

    [Fact]
    public void Analyze_PerStatePointersWrittenByC7WithEachModRmForm_AreAllRecognised()
    {
        // 类 4:过程入口 .text 偏移 0x70000、进入入口 0x70200。四个"逐状态函数"各自前面是 CC 填充,放在进入入口前 0x600/0x500/0x400/0x300 处,
        // 每个函数里是一个按键助手起手点(技能号 941/942/943/944 → 状态 0x31/0x33/0x34/0x35)。
        // 逐状态函数是否按"逐状态指针"登记,取决于它自己的入口有没有被指针扫描认出来(每个函数各自独立判定,不会被相邻函数带上)。
        // 类 4 的初始化代码(0x70060 起)里先有一个合成大偏移 dword 0x7134(落在 0x7000..0x8000,触发"逐状态指针数组"扫描),
        // 再用四种 C7 写法(三种 mov dword ptr [..], imm32 + 一种 mov r32, imm32)各写一个指向这些函数入口的代码指针。
        // 每条 C7 前面一个字节都不是 ModRM 的合理值:读前一字节会判到别的分支,立即数位置就错了。
        (SyntheticPeBuilder pe, uint[] vtables, uint keyHelperVa) = BuildClassSkeleton();
        const int cls = 4;
        const uint largeOffset = 0x7134;
        int procOff = ClassProcOff(cls); // 0x70000
        int[] fnOffs = { procOff - 0x600, procOff - 0x500, procOff - 0x400, procOff - 0x300 };
        int[] skills = { 941, 942, 943, 944 };
        int[] states = { 0x31, 0x33, 0x34, 0x35 };
        for (int i = 0; i < fnOffs.Length; i++)
        {
            int state = states[i];
            int skill = skills[i];
            EmitHubSite(pe, keyHelperVa, fnOffs[i], q => pe.EmitPushImm(q, skill), q => pe.EmitSetState(q, state));
        }

        int p = procOff + 0x60;
        BitConverter.GetBytes(largeOffset).CopyTo(pe.Text, p);
        p += 4;

        // 写法一(mod=01):mov dword ptr [eax+0x10], fn0  =>  C7 40 10 imm32;立即数在 +3;前一字节故意是 0x90(读成 mod=10 → 立即数在 +6)
        pe.Text[p++] = 0x90;
        pe.Text[p++] = 0xC7; pe.Text[p++] = 0x40; pe.Text[p++] = 0x10;
        BitConverter.GetBytes(pe.TextVa(fnOffs[0])).CopyTo(pe.Text, p);
        p += 4;

        // 写法二(mod=10):mov dword ptr [eax+0x7134], fn1  =>  C7 80 34 71 00 00 imm32;立即数在 +6;前一字节故意是 0xCC(读成默认 → 立即数在 +2)
        pe.Text[p++] = 0xCC;
        pe.Text[p++] = 0xC7; pe.Text[p++] = 0x80;
        BitConverter.GetBytes(largeOffset).CopyTo(pe.Text, p);
        p += 4;
        BitConverter.GetBytes(pe.TextVa(fnOffs[1])).CopyTo(pe.Text, p);
        p += 4;

        // 写法三(mod=00、rm=5):mov dword ptr [abs32], fn2  =>  C7 05 abs32 imm32;立即数在 +6;前一字节故意是 0xCC
        pe.Text[p++] = 0xCC;
        pe.Text[p++] = 0xC7; pe.Text[p++] = 0x05;
        BitConverter.GetBytes(pe.DataVa(0x100)).CopyTo(pe.Text, p);
        p += 4;
        BitConverter.GetBytes(pe.TextVa(fnOffs[2])).CopyTo(pe.Text, p);
        p += 4;

        // 写法四(mod=11,"其余"分支):mov ecx, fn3  =>  C7 C1 imm32;立即数在 +2;前一字节故意是 0x90(读成 mod=10 → 立即数在 +6)
        pe.Text[p++] = 0x90;
        pe.Text[p++] = 0xC7; pe.Text[p++] = 0xC1;
        BitConverter.GetBytes(pe.TextVa(fnOffs[3])).CopyTo(pe.Text, p);
        p += 4;
        pe.EmitRet(p);

        SkillCensus.NativeAnalysis analysis = AnalyzeSkeleton(pe);

        // 四个函数入口的指针都被认出来 → 函数里的起手点按"逐状态指针"登记(PerStatePointer),而不是普通的按键助手点(KeyHub)
        for (int i = 0; i < fnOffs.Length; i++)
        {
            Assert.Equal(new[] { new SkillCensus.EntryPoint(states[i], SkillCensus.EntryKind.PerStatePointer) },
                EntriesOf(analysis, vtables[cls], skills[i]));
        }
    }

    [Fact]
    public void Analyze_CallSiteWithoutAnySetStateCall_RecordsPureCallSentinelMinusOne()
    {
        // 起手点里没有 `mov edx,[eax+0x30c]`:只有一个无关的 `push 5; call edx`(没读 +0x30c 槽,不算置状态)。
        // 没找到任何状态时按"纯调用/切换"登记哨兵状态 -1;不是别的数。
        List<SkillCensus.EntryPoint> entries = EntriesOfSingleSite(961, (pe, q) =>
        {
            q = pe.EmitPushImm(q, 5);
            return pe.EmitCallEdx(q);
        });

        Assert.Equal(new[] { new SkillCensus.EntryPoint(-1, SkillCensus.EntryKind.KeyHub) }, entries);
        Assert.DoesNotContain(entries, e => e.StateId == -2);
        Assert.DoesNotContain(entries, e => e.StateId == 5);
    }

    [Fact]
    public void Analyze_StackConstantRunIds_KeepOneAndNineNineNineAndDropZeroAndOneThousand()
    {
        // 栈常量数组分支的"首项不校验范围":数组的第一项可以是 0 或 1000(非法),其后各项必须在 1..999 才继续。
        // 类 1 的起手点:[ebp-0x20]=0、[ebp-0x1c]=1、[ebp-0x18]=999 → 技能号序列 [0, 1, 999] → 0 被丢弃,1 与 999 保留(1 是合法下界)。
        // 类 2 的起手点:[ebp-0x20]=1000、[ebp-0x1c]=2、[ebp-0x18]=3 → 序列 [1000, 2, 3] → 1000 被丢弃,2 与 3 保留(证明这个点确实被处理过)。
        // 前导最后是 push eax(寄存器不是已知常量),所以技能号来源只能是栈常量数组。
        (SyntheticPeBuilder pe, uint[] vtables, uint keyHelperVa) = BuildClassSkeleton();
        EmitHubSite(pe, keyHelperVa, ClassProcOff(1) + 0x60, q =>
        {
            q = pe.EmitStackConst(q, -0x20, 0);
            q = pe.EmitStackConst(q, -0x1C, 1);
            q = pe.EmitStackConst(q, -0x18, 999);
            pe.Text[q++] = 0x50; // push eax
            return q;
        }, q => pe.EmitSetState(q, 0x2A));
        EmitHubSite(pe, keyHelperVa, ClassProcOff(2) + 0x60, q =>
        {
            q = pe.EmitStackConst(q, -0x20, 1000);
            q = pe.EmitStackConst(q, -0x1C, 2);
            q = pe.EmitStackConst(q, -0x18, 3);
            pe.Text[q++] = 0x50; // push eax
            return q;
        }, q => pe.EmitSetState(q, 0x2A));

        SkillCensus.NativeAnalysis analysis = AnalyzeSkeleton(pe);

        // 全部类里登记过的技能号恰好是 {1, 2, 3, 999}:0 与 1000 一个都不在
        int[] allIds = analysis.ClassEntries.Values.SelectMany(m => m.Keys).OrderBy(k => k).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 999 }, allIds);

        var loopTable = new[] { new SkillCensus.EntryPoint(0x2A, SkillCensus.EntryKind.LoopTable) };
        Assert.Equal(loopTable, EntriesOf(analysis, vtables[1], 1));
        Assert.Equal(loopTable, EntriesOf(analysis, vtables[1], 999));
        Assert.Equal(loopTable, EntriesOf(analysis, vtables[2], 2));
        Assert.Equal(loopTable, EntriesOf(analysis, vtables[2], 3));
        Assert.DoesNotContain(0, analysis.ClassEntries[vtables[1]].Keys);
        Assert.DoesNotContain(1000, analysis.ClassEntries[vtables[2]].Keys);
    }

    [Fact]
    public void Analyze_AddEspBeforeSetStateCall_ClearsPushedArgumentsSoNoStateIsRecorded()
    {
        // `push 7; add esp,4; mov edx,[eax+0x30c]; call edx`:置状态调用前没有新的 push,栈已被 `add esp,4`(83 C4 04:mod=3、rm=4)清掉,
        // 7 不再是置状态的参数 → 不记 7,整个起手点落到"没找到状态"的 -1 哨兵。
        List<SkillCensus.EntryPoint> entries = EntriesOfSingleSite(971, (pe, q) =>
        {
            q = pe.EmitPushImm(q, 7);
            q = pe.EmitAddRegImm8(q, 0xC4, 4);
            q = pe.EmitMovEdxSetStateSlot(q);
            return pe.EmitCallEdx(q);
        });

        Assert.Equal(new[] { new SkillCensus.EntryPoint(-1, SkillCensus.EntryKind.KeyHub) }, entries);
        Assert.DoesNotContain(entries, e => e.StateId == 7);
    }

    [Fact]
    public void Analyze_AddToOtherRegisterBeforeSetStateCall_KeepsStackTopAsStateArgument()
    {
        // `push 9; add eax,4; mov edx,[eax+0x30c]; call edx`:`add` 的目标是 eax(83 C0 04:mod=3、rm=0)而不是 esp,不清栈,
        // 栈顶的 9 仍是置状态参数 → 必须记成状态 9(不是 -1)。
        List<SkillCensus.EntryPoint> entries = EntriesOfSingleSite(972, (pe, q) =>
        {
            q = pe.EmitPushImm(q, 9);
            q = pe.EmitAddRegImm8(q, 0xC0, 4);
            q = pe.EmitMovEdxSetStateSlot(q);
            return pe.EmitCallEdx(q);
        });

        Assert.Equal(new[] { new SkillCensus.EntryPoint(9, SkillCensus.EntryKind.KeyHub) }, entries);
        Assert.DoesNotContain(entries, e => e.StateId == -1);
    }
}
