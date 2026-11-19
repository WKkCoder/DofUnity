using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Dnf.Data;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using Dnf.Script;

[assembly: InternalsVisibleTo("Dnf.Play.Tests")]

namespace Dnf.Cli.Analysis;

/// <summary>
/// 只读技能普查命令(<c>dnf skill-census</c>):汇总全部职业脚本的主被动计数、已有配方口径下已能施放数、
/// 原生起手路径分类、可读出的客户端原生状态号，以及跨职业状态号直方图(按可解锁技能数降序)。
/// 输出与代码全程匿名，不打印任何真实职业编号、职业名、技能编号、技能名或素材路径。
/// </summary>
internal static class SkillCensus
{
    private static readonly string[] JobLabels =
    {
        "职业甲", "职业乙", "职业丙", "职业丁", "职业戊",
        "职业己", "职业庚", "职业辛", "职业壬", "职业癸", "职业子",
    };

    private static readonly HashSet<int> KnownSharedStates = new()
    {
        0x00, 0x05, 0x06, 0x0d, 0x0e, 0x0f, 0x10, 0x11, 0x12, 0xfe,
    };

    private const int BaseStateLimit = 0x13;
    private const uint PeSlotProcess = 0x1f0;
    private const uint PeSlotEnterState = 0x29c;
    private const uint PeSlotSetState = 0x30c;
    private const uint PeSlotKeyGetter = 0x918;
    private const uint PeSlotDelegateInit = 0xa20;
    private const uint JobStateHandlerArrayMinOffset = 0x7000;

    internal enum EntryKind
    {
        Squirrel,
        Dispatch,
        LoopTable,
        BaseBuff,
        PerStatePointer,
        KeyHub,
    }

    internal sealed record EntryPoint(int StateId, EntryKind Kind);

    internal sealed record PeSection(uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);

    internal sealed class PeImage
    {
        private readonly byte[] _bytes;
        private readonly PeSection[] _ordinarySections;
        private readonly uint _imageBase;

        private PeImage(byte[] bytes, uint imageBase, PeSection[] ordinarySections)
        {
            _bytes = bytes;
            _imageBase = imageBase;
            _ordinarySections = ordinarySections;
        }

        public uint TextStartVa => _ordinarySections.Length > 0 ? _imageBase + _ordinarySections[0].VirtualAddress : 0;
        public uint TextEndVa => _ordinarySections.Length > 0 ? TextStartVa + _ordinarySections[0].RawSize : 0;
        public uint TextRawOffset => _ordinarySections.Length > 0 ? _ordinarySections[0].RawOffset : 0;
        public int TextRawSize => _ordinarySections.Length > 0 ? (int)_ordinarySections[0].RawSize : 0;

        public uint RdataStartVa => _ordinarySections.Length > 1 ? _imageBase + _ordinarySections[1].VirtualAddress : 0;
        public uint RdataEndVa => _ordinarySections.Length > 1 ? RdataStartVa + _ordinarySections[1].RawSize : 0;
        public uint RdataRawOffset => _ordinarySections.Length > 1 ? _ordinarySections[1].RawOffset : 0;
        public int RdataRawSize => _ordinarySections.Length > 1 ? (int)_ordinarySections[1].RawSize : 0;

        public byte[] RawBytes => _bytes;

        public static PeImage? TryLoad(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            return TryParse(bytes);
        }

        public static PeImage? TryParse(byte[] bytes)
        {
            if (bytes.Length < 0x100 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                return null;
            }
            int peOff = BitConverter.ToInt32(bytes, 0x3c);
            if (peOff < 0 || peOff + 24 + 96 > bytes.Length ||
                bytes[peOff] != (byte)'P' || bytes[peOff + 1] != (byte)'E' ||
                bytes[peOff + 2] != 0 || bytes[peOff + 3] != 0)
            {
                return null;
            }
            ushort nSec = BitConverter.ToUInt16(bytes, peOff + 6);
            ushort optSize = BitConverter.ToUInt16(bytes, peOff + 20);
            int optOff = peOff + 24;
            if (optOff + optSize > bytes.Length || optSize < 32)
            {
                return null;
            }
            uint imageBase = BitConverter.ToUInt32(bytes, optOff + 28);
            int secTableOff = optOff + optSize;
            int ordinaryCount = Math.Min(3, (int)nSec);
            if (secTableOff + ordinaryCount * 40 > bytes.Length)
            {
                return null;
            }
            // 仅读取 PE 前三个常规节(.text/.rdata/.data)，绝不碰后续受保护节
            var secs = new PeSection[ordinaryCount];
            for (int i = 0; i < ordinaryCount; i++)
            {
                int o = secTableOff + i * 40;
                uint vSize = BitConverter.ToUInt32(bytes, o + 8);
                uint vAddr = BitConverter.ToUInt32(bytes, o + 12);
                uint rSize = BitConverter.ToUInt32(bytes, o + 16);
                uint rPtr = BitConverter.ToUInt32(bytes, o + 20);
                if (rPtr + rSize > (uint)bytes.Length)
                {
                    return null;
                }
                secs[i] = new PeSection(vAddr, vSize, rPtr, rSize);
            }
            return new PeImage(bytes, imageBase, secs);
        }

        public bool IsTextVa(uint va) => va >= TextStartVa && va < TextEndVa;
        public bool IsRdataVa(uint va) => va >= RdataStartVa && va < RdataEndVa;
        public bool IsStaticDataVa(uint va)
        {
            if (va < _imageBase) return false;
            uint rva = va - _imageBase;
            for (int i = 1; i < _ordinarySections.Length; i++)
            {
                PeSection s = _ordinarySections[i];
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.RawSize)
                {
                    return true;
                }
            }
            return false;
        }

        public bool TryVaToOff(uint va, out int off)
        {
            if (va >= _imageBase)
            {
                uint rva = va - _imageBase;
                for (int i = 0; i < _ordinarySections.Length; i++)
                {
                    PeSection s = _ordinarySections[i];
                    if (rva >= s.VirtualAddress && rva < s.VirtualAddress + s.RawSize)
                    {
                        off = (int)(s.RawOffset + (rva - s.VirtualAddress));
                        return off >= 0 && off < _bytes.Length;
                    }
                }
            }
            off = -1;
            return false;
        }

        public bool TryReadU32(uint va, out uint val)
        {
            if (TryVaToOff(va, out int off) && off + 4 <= _bytes.Length)
            {
                val = BitConverter.ToUInt32(_bytes, off);
                return true;
            }
            val = 0;
            return false;
        }
    }

    internal enum X86OpKind
    {
        Other,
        Push,
        Mov,
        Xor,
        Cmp,
        Test,
        Add,
        Sub,
        Lea,
        Call,
        Jmp,
        Jcc,
        Ret,
    }

    internal readonly struct X86Insn
    {
        public uint Va { get; init; }
        public int Length { get; init; }
        public X86OpKind Kind { get; init; }
        public byte Opcode { get; init; }
        public byte Opcode2 { get; init; }
        public bool HasModRm { get; init; }
        public byte Mod { get; init; }
        public byte Reg { get; init; }
        public byte Rm { get; init; }
        public bool HasSib { get; init; }
        public byte SibBase { get; init; }
        public bool HasDisp { get; init; }
        public int Disp { get; init; }
        public bool HasImm { get; init; }
        public int Imm { get; init; }
        public bool HasRelTarget { get; init; }
        public uint RelTarget { get; init; }

        public bool ReadsMemDisp(uint exactDisp) => HasModRm && Mod != 3 && HasDisp && unchecked((uint)Disp) == exactDisp;
    }

    internal static bool TryDecodeX86(byte[] buf, int pos, int limit, uint va, out X86Insn insn)
    {
        insn = default;
        if (pos < 0 || pos >= limit || pos >= buf.Length)
        {
            return false;
        }
        int start = pos;
        int end = Math.Min(limit, buf.Length);
        bool op16 = false;
        bool addr16 = false;
        while (pos < end)
        {
            byte p = buf[pos];
            if (p == 0x66) { op16 = true; pos++; }
            else if (p == 0x67) { addr16 = true; pos++; }
            else if (p is 0xF0 or 0xF2 or 0xF3 or 0x26 or 0x2E or 0x36 or 0x3E or 0x64 or 0x65) { pos++; }
            else { break; }
        }
        if (pos >= end)
        {
            return false;
        }

        byte op = buf[pos++];
        byte op2 = 0;
        bool twoByte = false;
        if (op == 0x0F)
        {
            if (pos >= end)
            {
                return false;
            }
            twoByte = true;
            op2 = buf[pos++];
            if (op2 is 0x38 or 0x3A)
            {
                if (pos >= end)
                {
                    return false;
                }
                pos++;
            }
        }

        bool hasModRm = false;
        int immKind = 0; // 1=i8, 2=i16, 4=opSize(i16/i32), 6=i16+i8, 7=i16+i32, 8=rel8, 9=rel32, 10=moffs
        X86OpKind kind = X86OpKind.Other;

        if (twoByte)
        {
            if (op2 >= 0x80 && op2 <= 0x8F)
            {
                kind = X86OpKind.Jcc;
                immKind = op16 ? 2 : 9;
            }
            else if (op2 == 0x3A)
            {
                hasModRm = true;
                immKind = 1;
            }
            else if (op2 is 0x70 or 0x71 or 0x72 or 0x73 or 0xA4 or 0xAC or 0xBA or 0xC2 or 0xC4 or 0xC5 or 0xC6)
            {
                hasModRm = true;
                immKind = 1;
            }
            else if (op2 is 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0B or 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 or 0x77 or 0xA0 or 0xA1 or 0xA2 or 0xA8 or 0xA9 or 0xAA)
            {
                hasModRm = false;
            }
            else if (op2 is >= 0xC8 and <= 0xCF)
            {
                hasModRm = false;
            }
            else
            {
                hasModRm = true;
            }
        }
        else
        {
            if (op < 0x40)
            {
                int lo = op & 0x0F;
                if (lo is 0x00 or 0x01 or 0x02 or 0x03 or 0x08 or 0x09 or 0x0A or 0x0B)
                {
                    hasModRm = true;
                    if (op is >= 0x30 and <= 0x33) kind = X86OpKind.Xor;
                    else if (op is >= 0x38 and <= 0x3B) kind = X86OpKind.Cmp;
                    else if (op is >= 0x00 and <= 0x03) kind = X86OpKind.Add;
                    else if (op is >= 0x28 and <= 0x2B) kind = X86OpKind.Sub;
                }
                else if (lo is 0x04 or 0x0C)
                {
                    immKind = 1;
                    if (op is 0x3C) kind = X86OpKind.Cmp;
                }
                else if (lo is 0x05 or 0x0D)
                {
                    immKind = 4;
                    if (op is 0x3D) kind = X86OpKind.Cmp;
                }
            }
            else if (op is >= 0x50 and <= 0x57) { kind = X86OpKind.Push; }
            else if (op is >= 0x58 and <= 0x5F) { }
            else if (op is >= 0x40 and <= 0x4F) { }
            else if (op is 0x62 or 0x63) { hasModRm = true; }
            else if (op == 0x68) { kind = X86OpKind.Push; immKind = 4; }
            else if (op == 0x69) { hasModRm = true; immKind = 4; }
            else if (op == 0x6A) { kind = X86OpKind.Push; immKind = 1; }
            else if (op == 0x6B) { hasModRm = true; immKind = 1; }
            else if (op is >= 0x70 and <= 0x7F) { kind = X86OpKind.Jcc; immKind = 8; }
            else if (op is 0x80 or 0x82 or 0x83) { hasModRm = true; immKind = 1; }
            else if (op == 0x81) { hasModRm = true; immKind = 4; }
            else if (op is 0x84 or 0x85) { hasModRm = true; kind = X86OpKind.Test; }
            else if (op is >= 0x88 and <= 0x8C or 0x8E) { hasModRm = true; if (op is >= 0x88 and <= 0x8B) kind = X86OpKind.Mov; }
            else if (op == 0x8D) { hasModRm = true; kind = X86OpKind.Lea; }
            else if (op == 0x8F) { hasModRm = true; }
            else if (op == 0x9A) { immKind = 7; }
            else if (op is >= 0xA0 and <= 0xA3) { immKind = 10; if (op is 0xA0 or 0xA1) kind = X86OpKind.Mov; }
            else if (op == 0xA8) { kind = X86OpKind.Test; immKind = 1; }
            else if (op == 0xA9) { kind = X86OpKind.Test; immKind = 4; }
            else if (op is >= 0xB0 and <= 0xB7) { kind = X86OpKind.Mov; immKind = 1; }
            else if (op is >= 0xB8 and <= 0xBF) { kind = X86OpKind.Mov; immKind = 4; }
            else if (op is 0xC0 or 0xC1) { hasModRm = true; immKind = 1; }
            else if (op == 0xC2) { kind = X86OpKind.Ret; immKind = 2; }
            else if (op == 0xC3) { kind = X86OpKind.Ret; }
            else if (op is 0xC4 or 0xC5) { hasModRm = true; }
            else if (op == 0xC6) { hasModRm = true; immKind = 1; kind = X86OpKind.Mov; }
            else if (op == 0xC7) { hasModRm = true; immKind = 4; kind = X86OpKind.Mov; }
            else if (op == 0xC8) { immKind = 6; }
            else if (op == 0xCA) { kind = X86OpKind.Ret; immKind = 2; }
            else if (op == 0xCB) { kind = X86OpKind.Ret; }
            else if (op == 0xCD) { immKind = 1; }
            else if (op is >= 0xD0 and <= 0xD3) { hasModRm = true; }
            else if (op is 0xD4 or 0xD5) { immKind = 1; }
            else if (op is >= 0xD8 and <= 0xDF) { hasModRm = true; }
            else if (op is >= 0xE0 and <= 0xE3) { kind = X86OpKind.Jcc; immKind = 8; }
            else if (op is >= 0xE4 and <= 0xE7) { immKind = 1; }
            else if (op == 0xE8) { kind = X86OpKind.Call; immKind = 9; }
            else if (op == 0xE9) { kind = X86OpKind.Jmp; immKind = 9; }
            else if (op == 0xEA) { immKind = 7; }
            else if (op == 0xEB) { kind = X86OpKind.Jmp; immKind = 8; }
            else if (op is 0xF6 or 0xF7) { hasModRm = true; immKind = op == 0xF6 ? -1 : -4; }
            else if (op is 0xFE or 0xFF) { hasModRm = true; }
        }

        byte mod = 0, reg = 0, rm = 0, sibBase = 0;
        bool hasSib = false, hasDisp = false;
        int disp = 0;

        if (hasModRm)
        {
            if (pos >= end)
            {
                return false;
            }
            byte m = buf[pos++];
            mod = (byte)((m >> 6) & 3);
            reg = (byte)((m >> 3) & 7);
            rm = (byte)(m & 7);

            if (!twoByte)
            {
                if (op is 0x80 or 0x81 or 0x82 or 0x83)
                {
                    if (reg == 0) kind = X86OpKind.Add;
                    else if (reg == 5) kind = X86OpKind.Sub;
                    else if (reg == 7) kind = X86OpKind.Cmp;
                }
                else if (op is 0xF6 or 0xF7)
                {
                    if (reg is 0 or 1)
                    {
                        kind = X86OpKind.Test;
                        immKind = op == 0xF6 ? 1 : 4;
                    }
                    else
                    {
                        immKind = 0;
                    }
                }
                else if (op == 0xFF)
                {
                    if (reg is 2 or 3) kind = X86OpKind.Call;
                    else if (reg is 4 or 5) kind = X86OpKind.Jmp;
                    else if (reg == 6) kind = X86OpKind.Push;
                }
            }

            if (addr16)
            {
                if ((mod == 0 && rm == 6) || mod == 2)
                {
                    if (pos + 2 > end) return false;
                    disp = BitConverter.ToInt16(buf, pos);
                    pos += 2;
                    hasDisp = true;
                }
                else if (mod == 1)
                {
                    if (pos + 1 > end) return false;
                    disp = (sbyte)buf[pos++];
                    hasDisp = true;
                }
            }
            else
            {
                if (mod != 3 && rm == 4)
                {
                    if (pos >= end) return false;
                    byte sib = buf[pos++];
                    hasSib = true;
                    sibBase = (byte)(sib & 7);
                    if (mod == 0 && sibBase == 5)
                    {
                        if (pos + 4 > end) return false;
                        disp = BitConverter.ToInt32(buf, pos);
                        pos += 4;
                        hasDisp = true;
                    }
                }
                else if (mod == 0 && rm == 5)
                {
                    if (pos + 4 > end) return false;
                    disp = BitConverter.ToInt32(buf, pos);
                    pos += 4;
                    hasDisp = true;
                }
                if (mod == 1)
                {
                    if (pos + 1 > end) return false;
                    disp = (sbyte)buf[pos++];
                    hasDisp = true;
                }
                else if (mod == 2)
                {
                    if (pos + 4 > end) return false;
                    disp = BitConverter.ToInt32(buf, pos);
                    pos += 4;
                    hasDisp = true;
                }
            }
        }
        else if (immKind < 0)
        {
            immKind = 0;
        }

        bool hasImm = false, hasRel = false;
        int imm = 0;
        uint relTarget = 0;
        switch (immKind)
        {
            case 1:
                if (pos + 1 > end) return false;
                imm = (sbyte)buf[pos++];
                hasImm = true;
                break;
            case 2:
                if (pos + 2 > end) return false;
                imm = BitConverter.ToInt16(buf, pos);
                pos += 2;
                hasImm = true;
                break;
            case 4:
                int sz = op16 ? 2 : 4;
                if (pos + sz > end) return false;
                imm = sz == 2 ? BitConverter.ToInt16(buf, pos) : BitConverter.ToInt32(buf, pos);
                pos += sz;
                hasImm = true;
                break;
            case 6:
                if (pos + 3 > end) return false;
                pos += 3;
                break;
            case 7:
                int psz = op16 ? 4 : 6;
                if (pos + psz > end) return false;
                pos += psz;
                break;
            case 8:
                if (pos + 1 > end) return false;
                int d8 = (sbyte)buf[pos++];
                relTarget = unchecked((uint)(va + (pos - start) + d8));
                hasRel = true;
                break;
            case 9:
                if (pos + 4 > end) return false;
                int d32 = BitConverter.ToInt32(buf, pos);
                pos += 4;
                relTarget = unchecked((uint)(va + (pos - start) + d32));
                hasRel = true;
                break;
            case 10:
                int msz = addr16 ? 2 : 4;
                if (pos + msz > end) return false;
                disp = msz == 2 ? BitConverter.ToUInt16(buf, pos) : BitConverter.ToInt32(buf, pos);
                pos += msz;
                hasDisp = true;
                break;
        }

        insn = new X86Insn
        {
            Va = va,
            Length = pos - start,
            Kind = kind,
            Opcode = op,
            Opcode2 = op2,
            HasModRm = hasModRm,
            Mod = mod,
            Reg = reg,
            Rm = rm,
            HasSib = hasSib,
            SibBase = sibBase,
            HasDisp = hasDisp,
            Disp = disp,
            HasImm = hasImm,
            Imm = imm,
            HasRelTarget = hasRel,
            RelTarget = relTarget,
        };
        return true;
    }

    internal sealed class ClassInfo
    {
        public uint VtableVa { get; init; }
        public uint ProcessVa { get; init; }
        public uint EnterStateVa { get; init; }
        public uint DelegateInitVa { get; init; }
        public uint RangeLo { get; set; }
        public uint RangeHi { get; set; }
    }

    internal sealed class NativeAnalysis
    {
        public Dictionary<int, List<uint>> JobToVtables { get; } = new();
        public HashSet<int> ConditionalBranchJobs { get; } = new();
        public Dictionary<uint, Dictionary<int, List<EntryPoint>>> ClassEntries { get; } = new();
        public Dictionary<int, List<EntryPoint>> SquirrelEntries { get; } = new();
    }

    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        PeImage? pe = PeImage.TryLoad(Path.Combine(loc.DnfDir, "DNF.exe"));
        return WriteReport(assets.Pvf, pe, Console.Out);
    }

    internal static int WriteReport(PvfArchive pvf, PeImage? pe, TextWriter output, Func<int, int, bool>? isCastable = null)
    {
        var data = new GameData(new PvfScriptSource(pvf));
        IReadOnlyDictionary<int, string>? charList = data.ReadList(GameData.CharacterListPath);
        if (charList == null || charList.Count == 0)
        {
            output.WriteLine("  [失败] 读不到角色表");
            return 1;
        }

        int[] jobIds = charList.Keys.OrderBy(k => k).ToArray();
        NativeAnalysis analysis = Analyze(pvf, pe, jobIds);
        Func<int, int, bool> castableFn = isCastable ?? IsCastableForJob;

        output.WriteLine($"技能普查: 角色脚本共 {jobIds.Length} 个, 客户端二进制={(pe != null ? "已读入普通节" : "缺失(仅统计脚本侧)")}");
        output.WriteLine();
        output.WriteLine("一、分职业技能与起手路径汇总(严格匿名编号)");
        output.WriteLine("职业   | 总技能 | 主动 | 被动 | 有指令主动 | 已有配方已能施放 | 起手匹配数(Squirrel/分派/循环表/基类/指针键/过程键/未匹配) | 可读原生状态数 | 原生状态集合");
        output.WriteLine(new string('-', 128));

        var histogram = new Dictionary<int, (HashSet<string> Jobs, int SkillCount, bool IsBase, HashSet<uint> LeafClasses, HashSet<int> ImplementedSkills)>();
        var allLabels = new List<string>(jobIds.Length);
        var totalImplementedByJob = 0;

        for (int idx = 0; idx < jobIds.Length; idx++)
        {
            int jobId = jobIds[idx];
            string label = idx < JobLabels.Length ? JobLabels[idx] : $"职业{idx + 1}";
            allLabels.Add(label);
            SkillCatalog catalog = SkillCatalog.Load(data, jobId);

            int total = catalog.Count;
            int active = 0;
            int passive = 0;
            int activeWithCmd = 0;
            int castable = 0;

            var byKind = new Dictionary<EntryKind, int>();
            int unmatched = 0;
            var jobStates = new SortedSet<int>();
            var skillToStates = new Dictionary<int, HashSet<int>>();

            foreach (SkillDef def in catalog.Skills)
            {
                if (!def.IsActive)
                {
                    passive++;
                    continue;
                }
                active++;
                if (def.HasCommand)
                {
                    activeWithCmd++;
                }

                bool impl = castableFn(jobId, def.Id);
                if (impl)
                {
                    castable++;
                }

                List<EntryPoint> entries = GatherEntriesForSkill(jobId, def.Id, analysis);
                if (entries.Count == 0)
                {
                    unmatched++;
                    continue;
                }

                EntryKind primary = ChoosePrimaryKind(entries);
                byKind[primary] = byKind.GetValueOrDefault(primary) + 1;

                foreach (EntryPoint ep in entries)
                {
                    if (ep.StateId < 0)
                    {
                        continue;
                    }
                    jobStates.Add(ep.StateId);
                    if (!skillToStates.TryGetValue(ep.StateId, out HashSet<int>? skSet))
                    {
                        skSet = new HashSet<int>();
                        skillToStates[ep.StateId] = skSet;
                    }
                    skSet.Add(def.Id);
                }
            }

            // 也将脚本/二进制可见但可能归于被动触发(如鼠标技)的 Squirrel 状态并入可读状态，但不混入主动计数
            if (analysis.SquirrelEntries.TryGetValue(jobId, out List<EntryPoint>? sqExtra))
            {
                foreach (EntryPoint ep in sqExtra)
                {
                    if (ep.StateId >= 0)
                    {
                        jobStates.Add(ep.StateId);
                    }
                }
            }

            uint leafVt = analysis.JobToVtables.TryGetValue(jobId, out var jvts) && jvts.Count > 0 ? jvts[^1] : (uint.MaxValue - (uint)jobId);
            foreach ((int st, HashSet<int> skSet) in skillToStates)
            {
                if (!histogram.TryGetValue(st, out var agg))
                {
                    agg = (new HashSet<string>(), 0, st < BaseStateLimit || KnownSharedStates.Contains(st), new HashSet<uint>(), new HashSet<int>());
                }
                agg.Jobs.Add(label);
                agg.SkillCount += skSet.Count;
                agg.LeafClasses.Add(leafVt);
                foreach (int sid in skSet)
                {
                    if (castableFn(jobId, sid))
                    {
                        agg.ImplementedSkills.Add((jobId << 16) ^ sid);
                    }
                }
                histogram[st] = agg;
            }

            totalImplementedByJob += castable;
            int sqC = byKind.GetValueOrDefault(EntryKind.Squirrel);
            int dpC = byKind.GetValueOrDefault(EntryKind.Dispatch);
            int lpC = byKind.GetValueOrDefault(EntryKind.LoopTable);
            int bbC = byKind.GetValueOrDefault(EntryKind.BaseBuff);
            int psC = byKind.GetValueOrDefault(EntryKind.PerStatePointer);
            int khC = byKind.GetValueOrDefault(EntryKind.KeyHub);
            string kindSummary = $"{sqC}/{dpC}/{lpC}/{bbC}/{psC}/{khC}/{unmatched}";
            string stateList = jobStates.Count == 0 ? "-" : string.Join(",", jobStates.Select(FormatHex));
            output.WriteLine($"{label,-4} | {total,6} | {active,4} | {passive,4} | {activeWithCmd,10} | {castable,16} | {kindSummary,-45} | {jobStates.Count,14} | {stateList}");
        }

        output.WriteLine();
        output.WriteLine($"全职业主动技能已有配方已能施放合计: {totalImplementedByJob} (按职业作用域与基类共享表统计)");
        output.WriteLine();
        output.WriteLine("二、跨职业原生状态号直方图(按可解锁主动技能数从大到小排序)");
        output.WriteLine("状态号 | 涉及职业数 | 关联主动技能数 | 作用域     | 已有配方状态 | 已实现关联技能数 | 涉及职业");
        output.WriteLine(new string('-', 104));

        foreach (KeyValuePair<int, (HashSet<string> Jobs, int SkillCount, bool IsBase, HashSet<uint> LeafClasses, HashSet<int> ImplementedSkills)> kv
            in histogram.OrderByDescending(k => k.Value.SkillCount).ThenByDescending(k => k.Value.Jobs.Count).ThenBy(k => k.Key))
        {
            int st = kv.Key;
            var v = kv.Value;
            string scope = v.IsBase ? "基类共享(同码)"
                : v.LeafClasses.Count == 1 ? (v.Jobs.Count > 1 ? "同C++类共享" : "单C++类局部")
                : "异类同号(各自状态)";
            string recipeStatus = DescribeStateRecipeCoverage(v.ImplementedSkills.Count, v.SkillCount);
            string jobsJoined = string.Join(",", allLabels.Where(v.Jobs.Contains));
            output.WriteLine($"{FormatHex(st),-6} | {v.Jobs.Count,10} | {v.SkillCount,14} | {scope,-16} | {recipeStatus,-14} | {v.ImplementedSkills.Count,16} | {jobsJoined}");
        }

        return 0;
    }

    internal static string FormatHex(int stateId) => "0x" + stateId.ToString("x2", CultureInfo.InvariantCulture);

    internal static bool IsCastableForJob(int jobId, int skillId)
    {
        return HeroSkills.IsImplemented(jobId, skillId);
    }

    internal static string DescribeStateRecipeCoverage(int implSkills, int totalSkills)
    {
        if (implSkills > 0 && implSkills >= totalSkills)
        {
            return "是(全覆盖)";
        }
        if (implSkills > 0)
        {
            return "部分(" + implSkills + "/" + totalSkills + ")";
        }
        return "否";
    }

    internal static EntryKind ChoosePrimaryKind(List<EntryPoint> entries)
    {
        EntryKind best = entries[0].Kind;
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Kind < best)
            {
                best = entries[i].Kind;
            }
        }
        return best;
    }

    private static List<EntryPoint> GatherEntriesForSkill(int jobId, int skillId, NativeAnalysis analysis)
    {
        var list = new List<EntryPoint>();
        if (analysis.JobToVtables.TryGetValue(jobId, out List<uint>? vtables))
        {
            foreach (uint vt in vtables)
            {
                if (analysis.ClassEntries.TryGetValue(vt, out var skillMap) &&
                    skillMap.TryGetValue(skillId, out List<EntryPoint>? pts))
                {
                    list.AddRange(pts);
                }
            }
        }
        uint sqKey = uint.MaxValue - (uint)jobId;
        if (analysis.ClassEntries.TryGetValue(sqKey, out var sqMap) &&
            sqMap.TryGetValue(skillId, out List<EntryPoint>? sqPts))
        {
            list.AddRange(sqPts);
        }
        return list;
    }

    internal static NativeAnalysis Analyze(PvfArchive pvf, PeImage? pe, int[] jobIds)
    {
        var analysis = new NativeAnalysis();
        if (pe != null)
        {
            AnalyzePe(pe, jobIds, analysis);
        }
        AnalyzeSquirrel(pvf, jobIds, analysis);
        return analysis;
    }

    private static uint SubZero(uint value, uint delta) => value > delta ? value - delta : 0;

    private static void AnalyzePe(PeImage pe, int[] jobIds, NativeAnalysis analysis)
    {
        List<ClassInfo> classes = DiscoverCharacterClasses(pe);
        if (classes.Count == 0)
        {
            return;
        }
        ClassInfo baseClass = classes[0];
        uint keyHelperVa = DiscoverKeyHelperVa(pe);

        // 根据每个职业的工厂 switch 推断它所对应的 C++ 类链表
        DiscoverJobInheritance(pe, classes, jobIds, analysis);
        AssignProcessRanges(classes);

        if (keyHelperVa != 0)
        {
            ScanKeyHelperSites(pe, classes, baseClass, keyHelperVa, analysis);
        }

        // 若某子类覆写了 delegate 表初始化(+0xa20)与分派表(+0x9c0),读取其中的 (skillKey -> handlerFn -> stateId)
        ScanDelegateInitializers(pe, classes, analysis);
    }

    internal static List<ClassInfo> DiscoverCharacterClasses(PeImage pe)
    {
        // 在 .rdata 中找共 11 张间隔 >= 0x400、同享 +0x918 按键读取虚槽指针、且 +0x1f0/+0x29c/+0x30c 指向 .text 的虚表
        var rdata = pe.RawBytes;
        int start = (int)pe.RdataRawOffset;
        int len = pe.RdataRawSize;
        var counts = new Dictionary<uint, List<uint>>();
        for (int off = 0; off + 4 <= len; off += 4)
        {
            uint val = BitConverter.ToUInt32(rdata, start + off);
            if (!pe.IsTextVa(val))
            {
                continue;
            }
            if (!counts.TryGetValue(val, out List<uint>? list))
            {
                list = new List<uint>();
                counts[val] = list;
            }
            list.Add(pe.RdataStartVa + (uint)off);
        }

        foreach (KeyValuePair<uint, List<uint>> kv in counts)
        {
            if (kv.Value.Count != 11)
            {
                continue;
            }
            bool spaced = true;
            for (int i = 0; i + 1 < kv.Value.Count; i++)
            {
                if (kv.Value[i + 1] - kv.Value[i] < 0x400)
                {
                    spaced = false;
                    break;
                }
            }
            if (!spaced)
            {
                continue;
            }

            // 验证减回 PeSlotKeyGetter 后各个 +0x1f0 / +0x29c / +0x30c 都是有效 .text 指针:
            // +0x1f0(process) 与 +0x29c(enterState) 必须在绝大多数类间各不相同(Distinct >= 9)，而 +0x30c(setState) 高度共享(Max >= 8)
            var cands = new List<ClassInfo>();
            var setStateCounts = new Dictionary<uint, int>();
            var processSet = new HashSet<uint>();
            var enterSet = new HashSet<uint>();
            bool valid = true;
            foreach (uint slotVa in kv.Value)
            {
                if (slotVa < PeSlotKeyGetter)
                {
                    valid = false;
                    break;
                }
                uint vt = slotVa - PeSlotKeyGetter;
                if (!pe.TryReadU32(vt + PeSlotProcess, out uint proc) || !pe.IsTextVa(proc) ||
                    !pe.TryReadU32(vt + PeSlotEnterState, out uint enter) || !pe.IsTextVa(enter) ||
                    !pe.TryReadU32(vt + PeSlotSetState, out uint setSt) || !pe.IsTextVa(setSt))
                {
                    valid = false;
                    break;
                }
                pe.TryReadU32(vt + PeSlotDelegateInit, out uint delInit);
                setStateCounts[setSt] = setStateCounts.GetValueOrDefault(setSt) + 1;
                processSet.Add(proc);
                enterSet.Add(enter);
                cands.Add(new ClassInfo
                {
                    VtableVa = vt,
                    ProcessVa = proc,
                    EnterStateVa = enter,
                    DelegateInitVa = pe.IsTextVa(delInit) ? delInit : 0,
                });
            }
            if (valid && setStateCounts.Values.Max() >= 8 && processSet.Count >= 9 && enterSet.Count >= 9)
            {
                cands.Sort((a, b) => a.VtableVa.CompareTo(b.VtableVa));
                return cands;
            }
        }
        return new List<ClassInfo>();
    }

    internal static uint DiscoverKeyHelperVa(PeImage pe)
    {
        // 在 .text 里查找紧跟在 `+0x918` 取虚函数之后、出现次数最高(>100次)的 `call rel32` 目标
        byte[] bytes = pe.RawBytes;
        int start = (int)pe.TextRawOffset;
        int len = pe.TextRawSize;
        var hits = new Dictionary<uint, int>();
        for (int i = 0; i + 16 < len; i++)
        {
            if (bytes[start + i] == 0x18 && bytes[start + i + 1] == 0x09 &&
                bytes[start + i + 2] == 0x00 && bytes[start + i + 3] == 0x00)
            {
                int scanEnd = Math.Min(len - 5, i + 28);
                for (int j = i + 4; j <= scanEnd; j++)
                {
                    if (bytes[start + j] == 0xE8)
                    {
                        uint srcVa = pe.TextStartVa + (uint)j;
                        int rel = BitConverter.ToInt32(bytes, start + j + 1);
                        uint target = unchecked((uint)(srcVa + 5 + rel));
                        if (pe.IsTextVa(target))
                        {
                            hits[target] = hits.GetValueOrDefault(target) + 1;
                        }
                    }
                }
            }
        }
        uint best = 0;
        int bestCount = 0;
        foreach (KeyValuePair<uint, int> kv in hits)
        {
            if (kv.Value > bestCount)
            {
                bestCount = kv.Value;
                best = kv.Key;
            }
        }
        return bestCount >= 100 ? best : 0;
    }

    private static void DiscoverJobInheritance(PeImage pe, List<ClassInfo> classes, int[] jobIds, NativeAnalysis analysis)
    {
        var knownVts = new HashSet<uint>(classes.Select(c => c.VtableVa));
        byte[] bytes = pe.RawBytes;
        int start = (int)pe.TextRawOffset;
        int len = pe.TextRawSize;
        int n = jobIds.Length;
        if (n < 2)
        {
            return;
        }

        // 先建立 ctorVa -> 直接写入的虚表集合 与 父类构造链
        var ctorCache = new Dictionary<uint, HashSet<uint>>();
        HashSet<uint> ResolveCtorVts(uint ctorVa, int depth)
        {
            if (ctorCache.TryGetValue(ctorVa, out HashSet<uint>? cached))
            {
                return cached;
            }
            var result = new HashSet<uint>();
            ctorCache[ctorVa] = result;
            if (depth > 5 || !pe.TryVaToOff(ctorVa, out int cOff))
            {
                return result;
            }
            int pos = cOff;
            int lim = Math.Min(bytes.Length, cOff + 0x200);
            while (pos < lim)
            {
                uint curVa = pe.TextStartVa + (uint)(pos - (int)pe.TextRawOffset);
                if (!TryDecodeX86(bytes, pos, lim, curVa, out X86Insn ins))
                {
                    break;
                }
                if (ins.Kind == X86OpKind.Ret)
                {
                    break;
                }
                if (ins.Kind == X86OpKind.Mov && ins.HasImm && knownVts.Contains(unchecked((uint)ins.Imm)))
                {
                    result.Add(unchecked((uint)ins.Imm));
                }
                else if (ins.Kind == X86OpKind.Call && ins.HasRelTarget && pe.IsTextVa(ins.RelTarget))
                {
                    HashSet<uint> sub = ResolveCtorVts(ins.RelTarget, depth + 1);
                    foreach (uint sv in sub)
                    {
                        result.Add(sv);
                    }
                }
                pos += ins.Length;
            }
            return result;
        }

        // 查找长度为 jobIds.Length 且各条目都在紧邻前方 0x400 字节内、并真正构造 Character 虚表的跳转表
        for (int off = 0; off + n * 4 <= len; off += 4)
        {
            uint tblVa = pe.TextStartVa + (uint)off;
            uint tblLo = SubZero(tblVa, 0x400);
            bool ok = true;
            for (int k = 0; k < n; k++)
            {
                uint t = BitConverter.ToUInt32(bytes, start + off + k * 4);
                if (t < tblLo || t >= tblVa)
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
            {
                continue;
            }

            var candMap = new Dictionary<int, List<uint>>();
            var branchJobs = new HashSet<int>();
            int resolvedJobs = 0;
            for (int idx = 0; idx < n; idx++)
            {
                int jid = jobIds[idx];
                uint caseEntry = BitConverter.ToUInt32(bytes, start + off + idx * 4);
                var vts = new HashSet<uint>();
                var directCtors = new HashSet<uint>();
                var visited = new HashSet<uint>();
                var q = new Queue<uint>();
                q.Enqueue(caseEntry);
                while (q.Count > 0 && visited.Count < 80)
                {
                    uint pc = q.Dequeue();
                    if (!visited.Add(pc) || !pe.TryVaToOff(pc, out int cOff))
                    {
                        continue;
                    }
                    int pos = cOff;
                    int lim = Math.Min(bytes.Length, cOff + 0x90);
                    while (pos < lim)
                    {
                        uint curVa = pe.TextStartVa + (uint)(pos - (int)pe.TextRawOffset);
                        if (!TryDecodeX86(bytes, pos, lim, curVa, out X86Insn ins))
                        {
                            break;
                        }
                        if (ins.Kind == X86OpKind.Ret)
                        {
                            break;
                        }
                        if (ins.Kind == X86OpKind.Call && ins.HasRelTarget && pe.IsTextVa(ins.RelTarget))
                        {
                            HashSet<uint> cVts = ResolveCtorVts(ins.RelTarget, 0);
                            if (cVts.Count > 0)
                            {
                                directCtors.Add(ins.RelTarget);
                                foreach (uint vt in cVts)
                                {
                                    vts.Add(vt);
                                }
                            }
                        }
                        if (ins.Kind == X86OpKind.Jmp)
                        {
                            if (ins.HasRelTarget && pe.IsTextVa(ins.RelTarget) && Math.Abs((long)ins.RelTarget - caseEntry) < 0x400)
                            {
                                q.Enqueue(ins.RelTarget);
                            }
                            break;
                        }
                        if (ins.Kind == X86OpKind.Jcc)
                        {
                            if (ins.HasRelTarget && pe.IsTextVa(ins.RelTarget) && Math.Abs((long)ins.RelTarget - caseEntry) < 0x400)
                            {
                                q.Enqueue(ins.RelTarget);
                            }
                            q.Enqueue(curVa + (uint)ins.Length);
                            break;
                        }
                        pos += ins.Length;
                    }
                }
                if (vts.Count > 0)
                {
                    resolvedJobs++;
                }
                if (directCtors.Count >= 2)
                {
                    branchJobs.Add(jid);
                }
                candMap[jid] = vts.OrderBy(v => v).ToList();
            }
            if (resolvedJobs == n)
            {
                foreach (var kv in candMap)
                {
                    analysis.JobToVtables[kv.Key] = kv.Value;
                }
                foreach (int bj in branchJobs)
                {
                    analysis.ConditionalBranchJobs.Add(bj);
                }
                return;
            }
        }
    }

    private static void AssignProcessRanges(List<ClassInfo> classes)
    {
        // 每个类的代码在 .text 中连续排布(主每帧处理 + 进入块等相邻辅助)，按 ProcessVa 区间结合特例处理指针集群归属
        var ordered = classes.OrderBy(c => c.ProcessVa).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            uint lo = i == 0 ? SubZero(ordered[i].ProcessVa, 0x40000) : ordered[i - 1].ProcessVa + (ordered[i].ProcessVa - ordered[i - 1].ProcessVa) / 2;
            uint hi = i + 1 < ordered.Count ? ordered[i].ProcessVa + (ordered[i + 1].ProcessVa - ordered[i].ProcessVa) / 2 : ordered[i].ProcessVa + 0x20000;
            // BaseChar 的函数簇靠近首职业类/BaseChar，保持足够覆盖基类键核；每个职业类按 EnterStateVa/ProcessVa 的边界扩展
            ordered[i].RangeLo = Math.Min(lo, ordered[i].EnterStateVa > 0 ? SubZero(ordered[i].EnterStateVa, 0x20000) : lo);
            ordered[i].RangeHi = Math.Max(hi, ordered[i].EnterStateVa > 0 ? ordered[i].EnterStateVa + 0x18000 : hi);
        }
    }

    private static ClassInfo? ResolveOwnerClass(uint callVa, List<ClassInfo> classes, PeImage pe)
    {
        // 精确归属优先级: 1) 直接落在某类 [min(ProcessVa,EnterStateVa)-0x25000, max(ProcessVa,EnterStateVa)+0x18000] 内
        // 按到 ProcessVa / EnterStateVa 距离最近的类归属；再核对是否是“逐状态函数指针表(+0x70a8)”类(被 .text 指针写入簇引用)
        ClassInfo? best = null;
        ulong bestDist = ulong.MaxValue;
        foreach (ClassInfo cls in classes)
        {
            uint mid = cls.ProcessVa;
            uint alt = cls.EnterStateVa;
            ulong d1 = callVa >= mid ? callVa - mid : mid - callVa;
            ulong d2 = callVa >= alt ? callVa - alt : alt - callVa;
            ulong d = Math.Min(d1, d2);
            if (d < bestDist)
            {
                bestDist = d;
                best = cls;
            }
        }
        return bestDist <= 0x35000 ? best : null;
    }

    private static HashSet<uint> FindPerStateHandlerFunctions(PeImage pe, List<ClassInfo> classes)
    {
        // 识别大偏移(+0x7000..+0x8000)状态指针数组初始化期间引用到的逐状态函数指针(位于 CC 对齐函数起点且靠近某类的 RangeLo..EnterStateVa)
        var result = new HashSet<uint>();
        byte[] bytes = pe.RawBytes;
        int start = (int)pe.TextRawOffset;
        int len = pe.TextRawSize;
        foreach (ClassInfo cls in classes)
        {
            if (cls.ProcessVa == 0 || cls.EnterStateVa == 0)
            {
                continue;
            }
            bool hasLargeOffset = false;
            if (pe.TryVaToOff(SubZero(cls.EnterStateVa, 0x10000) > pe.TextStartVa ? cls.EnterStateVa : pe.TextStartVa, out _))
            {
                uint scanStartVa = Math.Max(pe.TextStartVa, SubZero(Math.Min(cls.ProcessVa, cls.EnterStateVa), 0x4000));
                uint scanEndVa = Math.Min(pe.TextEndVa, Math.Max(cls.ProcessVa, cls.EnterStateVa) + 0x10000);
                if (pe.TryVaToOff(scanStartVa, out int sOff) && pe.TryVaToOff(scanEndVa, out int eOff))
                {
                    for (int p = sOff; p + 4 <= eOff; p++)
                    {
                        uint val = BitConverter.ToUInt32(bytes, p);
                        if (val >= JobStateHandlerArrayMinOffset && val < 0x8000)
                        {
                            hasLargeOffset = true;
                            break;
                        }
                    }
                    if (hasLargeOffset)
                    {
                        // 扫描附近初始化写入的指向前序子函数集群的代码指针(以 CC 对齐为特征)
                        for (int p = sOff; p + 5 <= eOff; p++)
                        {
                            byte op = bytes[p];
                            if ((op >= 0xB8 && op <= 0xBF) || op == 0x68 || op == 0xC7)
                            {
                                int immOff = op == 0xC7 ? p + 2 : p + 1;
                                if (op == 0xC7 && p + 6 <= eOff)
                                {
                                    // 跳过非直接 disp 的复杂 modrm；主要抓 C7 0x / B8..BF / 68
                                    byte m = bytes[p + 1];
                                    if ((m & 0xC0) == 0x80) immOff = p + 6;
                                    else if ((m & 0xC0) == 0x40) immOff = p + 3;
                                    else if ((m & 0xC7) == 0x05) immOff = p + 6;
                                }
                                if (immOff + 4 <= eOff)
                                {
                                    uint imm = BitConverter.ToUInt32(bytes, immOff);
                                    if (pe.IsTextVa(imm) && imm < cls.EnterStateVa && cls.EnterStateVa - imm <= 0x40000 &&
                                        pe.TryVaToOff(imm, out int tOff) && tOff > start && bytes[tOff - 1] == 0xCC)
                                    {
                                        result.Add(imm);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        return result;
    }

    private static void ScanKeyHelperSites(PeImage pe, List<ClassInfo> classes, ClassInfo baseClass, uint keyHelperVa, NativeAnalysis analysis)
    {
        byte[] bytes = pe.RawBytes;
        int start = (int)pe.TextRawOffset;
        int len = pe.TextRawSize;
        HashSet<uint> perStateHandlers = FindPerStateHandlerFunctions(pe, classes);

        // 也顺带识别含 [ebp - x] 固定数组的小循环(如压栈 6 个增益技能号，进入状态 0x11)
        var callSites = new List<uint>();
        for (int i = 0; i + 5 <= len; i++)
        {
            if (bytes[start + i] == 0xE8)
            {
                uint srcVa = pe.TextStartVa + (uint)i;
                int rel = BitConverter.ToInt32(bytes, start + i + 1);
                if (unchecked((uint)(srcVa + 5 + rel)) == keyHelperVa)
                {
                    callSites.Add(srcVa);
                }
            }
        }

        // 先找出哪个类的区间最接近每处 perStateHandlers，将其函数起止范围算作该逐状态指针类
        ClassInfo? statePtrOwner = null;
        if (perStateHandlers.Count > 0)
        {
            uint minFn = perStateHandlers.Min();
            uint maxFn = perStateHandlers.Max();
            statePtrOwner = ResolveOwnerClass(minFn, classes, pe) ?? ResolveOwnerClass(maxFn, classes, pe);
        }

        foreach (uint callVa in callSites)
        {
            bool isPerStatePtr = IsInsidePerStateHandlerCluster(callVa, perStateHandlers, bytes, start, pe);
            ClassInfo? owner = isPerStatePtr && statePtrOwner != null ? statePtrOwner : ResolveOwnerClass(callVa, classes, pe);
            if (owner == null)
            {
                continue;
            }

            ExtractCallSite(pe, callVa, keyHelperVa, owner, baseClass, isPerStatePtr, analysis);
        }
    }

    private static bool IsInsidePerStateHandlerCluster(uint callVa, HashSet<uint> perStateHandlers, byte[] bytes, int textOff, PeImage pe)
    {
        if (perStateHandlers.Count == 0 || !pe.TryVaToOff(callVa, out int off))
        {
            return false;
        }
        // 检查向回最近的 CC CC CC CC 填充之后的起点是否命中 perStateHandlers
        int backLimit = Math.Max(textOff, off - 0x2000);
        for (int p = off - 4; p >= backLimit; p--)
        {
            if (bytes[p] == 0xCC && bytes[p + 1] == 0xCC && bytes[p + 2] == 0xCC && bytes[p + 3] == 0xCC)
            {
                int fnOff = p + 4;
                while (fnOff < off && bytes[fnOff] == 0xCC) fnOff++;
                uint fnVa = pe.TextStartVa + (uint)(fnOff - textOff);
                return perStateHandlers.Contains(fnVa);
            }
        }
        return false;
    }

    private static void ExtractCallSite(PeImage pe, uint callVa, uint keyHelperVa, ClassInfo owner, ClassInfo baseClass, bool isPerStatePtr, NativeAnalysis analysis)
    {
        if (!pe.TryVaToOff(callVa, out int callOff))
        {
            return;
        }
        byte[] bytes = pe.RawBytes;
        int textStart = (int)pe.TextRawOffset;
        int textEnd = textStart + pe.TextRawSize;

        // 找到能够准确对齐到 callVa 的前导起始位置
        int blockStartOff = FindAlignedPreStart(bytes, textStart, textEnd, callOff, callVa, pe.TextStartVa);
        var preInsns = new List<X86Insn>();
        int p = blockStartOff;
        while (p <= callOff)
        {
            uint curVa = pe.TextStartVa + (uint)(p - textStart);
            if (!TryDecodeX86(bytes, p, textEnd, curVa, out X86Insn ins))
            {
                break;
            }
            if (curVa == callVa)
            {
                break;
            }
            preInsns.Add(ins);
            p += ins.Length;
        }

        var regs = new int?[8];
        var pushes = new List<int?>();
        uint rdataArrBase = 0;
        bool rdataIsPtrLoop = false;
        var stackConstants = new Dictionary<int, int>();

        foreach (X86Insn ins in preInsns)
        {
            if (ins.Kind == X86OpKind.Mov)
            {
                if (ins.Opcode >= 0xB8 && ins.Opcode <= 0xBF && ins.HasImm)
                {
                    int regIdx = ins.Opcode - 0xB8;
                    regs[regIdx] = ins.Imm;
                    uint uimm = unchecked((uint)ins.Imm);
                    if (pe.IsStaticDataVa(uimm))
                    {
                        rdataArrBase = uimm;
                        rdataIsPtrLoop = true;
                    }
                }
                else if (ins.Opcode == 0xC7 && ins.HasModRm && ins.Mod != 3 && ins.Rm == 5 && ins.HasDisp && ins.HasImm)
                {
                    stackConstants[ins.Disp] = ins.Imm;
                }
                else if (ins.Opcode == 0x8B && ins.HasModRm)
                {
                    if (ins.Mod != 3 && ins.HasDisp && pe.IsStaticDataVa(unchecked((uint)ins.Disp)))
                    {
                        rdataArrBase = unchecked((uint)ins.Disp);
                        rdataIsPtrLoop = false;
                    }
                    // `mov reg, ...` 覆写目标寄存器, 必须清空之前缓存的常数, 防止陈旧常量污染后续 push
                    regs[ins.Reg] = ins.Mod == 3 ? regs[ins.Rm] : null;
                }
            }
            else if (ins.Kind == X86OpKind.Lea && ins.HasModRm)
            {
                regs[ins.Reg] = null;
            }
            else if (ins.Kind == X86OpKind.Xor && ins.HasModRm && ins.Mod == 3 && ins.Reg == ins.Rm)
            {
                regs[ins.Reg] = 0;
            }
            else if (ins.Kind == X86OpKind.Call)
            {
                regs[0] = null;
                regs[1] = null;
                regs[2] = null;
            }
            else if (ins.Kind == X86OpKind.Push)
            {
                if (ins.HasImm)
                {
                    pushes.Add(ins.Imm);
                }
                else if (ins.Opcode >= 0x50 && ins.Opcode <= 0x57)
                {
                    pushes.Add(regs[ins.Opcode - 0x50]);
                }
                else if (ins.Opcode == 0xFF && ins.HasModRm && ins.Mod == 3)
                {
                    pushes.Add(regs[ins.Rm]);
                }
                else
                {
                    pushes.Add(null);
                }
            }
        }

        // 判断技能号来源：直接立即数 / 栈常量数组循环 / .rdata 常数组表循环
        var skillIds = new List<int>();
        EntryKind kind = isPerStatePtr ? EntryKind.PerStatePointer : EntryKind.KeyHub;

        int? lastPush = pushes.Count > 0 ? pushes[^1] : null;
        if (lastPush.HasValue && lastPush.Value > 0 && lastPush.Value < 1000)
        {
            skillIds.Add(lastPush.Value);
        }
        else if (rdataArrBase != 0)
        {
            kind = EntryKind.LoopTable;
            ReadLoopTableSkillIds(pe, rdataArrBase, rdataIsPtrLoop, callVa, skillIds);
        }
        else if (stackConstants.Count >= 3)
        {
            // 检查诸如 [ebp-0x838..ebp-0x824] 紧邻连续的栈常量技能表
            List<KeyValuePair<int, int>> sorted = stackConstants.OrderBy(kv => kv.Key).ToList();
            for (int s = 0; s < sorted.Count; s++)
            {
                var run = new List<int> { sorted[s].Value };
                int prevDisp = sorted[s].Key;
                for (int t = s + 1; t < sorted.Count && sorted[t].Key == prevDisp + 4; t++)
                {
                    if (sorted[t].Value <= 0 || sorted[t].Value >= 1000)
                    {
                        break;
                    }
                    run.Add(sorted[t].Value);
                    prevDisp = sorted[t].Key;
                }
                if (run.Count >= 3)
                {
                    kind = EntryKind.LoopTable;
                    skillIds.AddRange(run);
                    break;
                }
            }
        }

        if (skillIds.Count == 0)
        {
            return;
        }

        // 查找 `cmp ..., -1; je skipTarget` 作为块结束界限，避免串入下一个技能判断块
        uint skipLimit = callVa + 0x1200;
        uint entryPc = callVa + 5;
        if (pe.TryVaToOff(callVa + 5, out int afterOff))
        {
            bool sawCmpMinusOne = false;
            int scanPos = afterOff;
            for (int k = 0; k < 24 && scanPos < textEnd; k++)
            {
                uint cVa = pe.TextStartVa + (uint)(scanPos - textStart);
                if (!TryDecodeX86(bytes, scanPos, textEnd, cVa, out X86Insn ins))
                {
                    break;
                }
                if (ins.Kind == X86OpKind.Cmp && ins.HasImm && ins.Imm == -1)
                {
                    sawCmpMinusOne = true;
                }
                else if (sawCmpMinusOne && ins.Kind == X86OpKind.Jcc && IsJeOpcode(ins))
                {
                    if (ins.HasRelTarget && ins.RelTarget > cVa)
                    {
                        skipLimit = ins.RelTarget;
                        entryPc = cVa + (uint)ins.Length;
                        break;
                    }
                }
                else if (sawCmpMinusOne && ins.Kind == X86OpKind.Jcc && IsJneOpcode(ins))
                {
                    sawCmpMinusOne = false;
                }
                scanPos += ins.Length;
            }
        }

        List<int> states = TraceSetStates(pe, callVa, entryPc, skipLimit, keyHelperVa, owner, baseClass, allowCallDescent: true);
        if (states.Count == 0)
        {
            // 若是未进入任何状态的开关/被动型或纯动作调用，依然记录为命中(状态 -1 表示纯调用/切换)
            states.Add(-1);
        }

        if (!analysis.ClassEntries.TryGetValue(owner.VtableVa, out var map))
        {
            map = new Dictionary<int, List<EntryPoint>>();
            analysis.ClassEntries[owner.VtableVa] = map;
        }
        foreach (int sid in skillIds)
        {
            if (sid <= 0 || sid >= 1000)
            {
                continue;
            }
            if (!map.TryGetValue(sid, out List<EntryPoint>? pts))
            {
                pts = new List<EntryPoint>();
                map[sid] = pts;
            }
            foreach (int st in states)
            {
                EntryPoint ep = new EntryPoint(st, owner.VtableVa == baseClass.VtableVa && kind == EntryKind.LoopTable ? EntryKind.BaseBuff : kind);
                if (!pts.Contains(ep))
                {
                    pts.Add(ep);
                }
            }
        }
    }

    private static void ReadLoopTableSkillIds(PeImage pe, uint arrBase, bool isPtrLoop, uint callVa, List<int> outSkills)
    {
        // 在附近 [.text] 找 `cmp reg, endVa`(指针步进) 或 `cmp idx, count`(下标步进)，上限 60 项并验证读出的值像合法技能号
        byte[] bytes = pe.RawBytes;
        int textStart = (int)pe.TextRawOffset;
        int textEnd = textStart + pe.TextRawSize;
        if (!pe.TryVaToOff(Math.Max(pe.TextStartVa, SubZero(callVa, 16)), out int startOff))
        {
            return;
        }
        int endOff = Math.Min(textEnd, startOff + 0x1200);
        uint endVa = 0;
        int pos = Math.Max(textStart, startOff);
        while (pos < endOff)
        {
            uint curVa = pe.TextStartVa + (uint)(pos - textStart);
            if (!TryDecodeX86(bytes, pos, textEnd, curVa, out X86Insn ins))
            {
                pos++;
                continue;
            }
            if (ins.Kind == X86OpKind.Cmp && ins.HasImm)
            {
                uint uimm = unchecked((uint)ins.Imm);
                if (isPtrLoop && uimm > arrBase && uimm <= arrBase + 0x200)
                {
                    endVa = uimm;
                    break;
                }
                if (!isPtrLoop && curVa > callVa && ins.Imm is >= 2 and <= 60)
                {
                    endVa = arrBase + (uint)(ins.Imm * 4);
                    break;
                }
            }
            pos += ins.Length;
        }
        if (endVa <= arrBase)
        {
            // 回退扫描 .rdata：读取合理范围内的有效正整数(排除 -1 / 虚表指针等终止符)
            for (int k = 0; k < 32; k++)
            {
                if (!pe.TryReadU32(arrBase + (uint)(k * 4), out uint v) || v == 0 || v >= 1000)
                {
                    break;
                }
                outSkills.Add((int)v);
            }
            return;
        }
        uint count = (endVa - arrBase) / 4;
        for (uint k = 0; k < count && k < 64; k++)
        {
            if (pe.TryReadU32(arrBase + k * 4, out uint v) && v > 0 && v < 1000)
            {
                outSkills.Add((int)v);
            }
        }
    }

    private static List<int> TraceSetStates(PeImage pe, uint originVa, uint entryPc, uint skipLimit, uint keyHelperVa,
        ClassInfo owner, ClassInfo baseClass, bool allowCallDescent)
    {
        byte[] bytes = pe.RawBytes;
        int textStart = (int)pe.TextRawOffset;
        int textEnd = textStart + pe.TextRawSize;

        var queue = new Queue<(uint Pc, int?[] Regs, List<int?> Stk, bool SawSetState)>();
        queue.Enqueue((entryPc, new int?[8], new List<int?>(), false));
        var visited = new HashSet<uint>();
        var states = new SortedSet<int>();

        while (queue.Count > 0 && visited.Count < 1600)
        {
            var (pc, regs, stk, sawSetState) = queue.Dequeue();
            if (pc < originVa || pc >= skipLimit || !pe.TryVaToOff(pc, out int off))
            {
                continue;
            }
            int pos = off;
            while (pos < textEnd && visited.Count < 1600)
            {
                uint curVa = pe.TextStartVa + (uint)(pos - textStart);
                if (curVa >= skipLimit || !visited.Add(curVa))
                {
                    break;
                }
                if (!TryDecodeX86(bytes, pos, textEnd, curVa, out X86Insn ins))
                {
                    break;
                }

                if (ins.Kind == X86OpKind.Call)
                {
                    if (curVa != originVa && ins.HasRelTarget && ins.RelTarget == keyHelperVa)
                    {
                        break;
                    }
                    if (sawSetState && stk.Count > 0)
                    {
                        int? arg0 = stk[^1];
                        if (arg0.HasValue && arg0.Value >= 1 && arg0.Value <= 500)
                        {
                            states.Add(arg0.Value);
                        }
                        sawSetState = false;
                    }
                    else if (allowCallDescent && ins.HasRelTarget &&
                        ins.RelTarget >= owner.RangeLo && ins.RelTarget <= owner.RangeHi &&
                        Math.Abs((long)ins.RelTarget - owner.VtableVa) > 0)
                    {
                        // 仅当调用的是类自己附近的局部辅助/独立起手(如 0x0049xxxx)才浅下潜 1 层
                        if (ins.RelTarget < baseClass.ProcessVa || ins.RelTarget >= SubZero(owner.ProcessVa, 0x40000))
                        {
                            foreach (int st in TraceSetStates(pe, ins.RelTarget, ins.RelTarget, ins.RelTarget + 0x900, keyHelperVa, owner, baseClass, allowCallDescent: false))
                            {
                                states.Add(st);
                            }
                        }
                    }
                    stk.Clear();
                }
                else if (ins.Kind == X86OpKind.Ret)
                {
                    break;
                }
                else
                {
                    if (ins.ReadsMemDisp(PeSlotSetState))
                    {
                        sawSetState = true;
                    }
                    if (ins.Kind == X86OpKind.Mov && ins.Opcode is >= 0xB8 and <= 0xBF && ins.HasImm)
                    {
                        regs[ins.Opcode - 0xB8] = ins.Imm;
                    }
                    else if (ins.Kind == X86OpKind.Xor && ins.HasModRm && ins.Mod == 3 && ins.Reg == ins.Rm)
                    {
                        regs[ins.Reg] = 0;
                    }
                    else if (ins.Kind == X86OpKind.Push)
                    {
                        int? pushed = null;
                        if (ins.HasImm) pushed = ins.Imm;
                        else if (ins.Opcode is >= 0x50 and <= 0x57) pushed = regs[ins.Opcode - 0x50];
                        else if (ins.Opcode == 0xFF && ins.HasModRm && ins.Mod == 3) pushed = regs[ins.Rm];
                        stk.Add(pushed);
                        if (stk.Count > 12) stk.RemoveAt(0);
                    }
                    else if (ins.Kind == X86OpKind.Add && ins.HasModRm && ins.Mod == 3 && ins.Rm == 4)
                    {
                        stk.Clear();
                    }
                    else if (ins.Kind == X86OpKind.Jmp)
                    {
                        if (ins.HasRelTarget && ins.RelTarget > originVa && ins.RelTarget < skipLimit)
                        {
                            queue.Enqueue((ins.RelTarget, (int?[])regs.Clone(), new List<int?>(stk), sawSetState));
                        }
                        break;
                    }
                    else if (ins.Kind == X86OpKind.Jcc)
                    {
                        if (ins.HasRelTarget && ins.RelTarget > curVa && ins.RelTarget < skipLimit)
                        {
                            queue.Enqueue((ins.RelTarget, (int?[])regs.Clone(), new List<int?>(stk), sawSetState));
                        }
                        queue.Enqueue((curVa + (uint)ins.Length, (int?[])regs.Clone(), new List<int?>(stk), sawSetState));
                        break;
                    }
                }

                pos += ins.Length;
            }
        }
        return states.ToList();
    }

    private static void ScanDelegateInitializers(PeImage pe, List<ClassInfo> classes, NativeAnalysis analysis)
    {
        byte[] bytes = pe.RawBytes;
        int textStart = (int)pe.TextRawOffset;
        int textEnd = textStart + pe.TextRawSize;

        foreach (ClassInfo cls in classes)
        {
            if (cls.DelegateInitVa == 0 || !pe.TryVaToOff(cls.DelegateInitVa, out int off))
            {
                continue;
            }
            // 读取 +0xa20 里 `mov reg, fnPtr` ... `mov [ebp-x], skillKey` ... `call mapInsert` 模式
            int lim = Math.Min(textEnd, off + 0x1000);
            uint pendingHandler = 0;
            int pendingSkill = 0;
            int pos = off;
            while (pos < lim)
            {
                uint curVa = pe.TextStartVa + (uint)(pos - textStart);
                if (!TryDecodeX86(bytes, pos, textEnd, curVa, out X86Insn ins))
                {
                    break;
                }
                if (ins.Kind == X86OpKind.Ret)
                {
                    break;
                }
                if (ins.Kind == X86OpKind.Mov && ins.HasImm)
                {
                    uint uimm = unchecked((uint)ins.Imm);
                    if (pe.IsTextVa(uimm) && Math.Abs((long)uimm - cls.DelegateInitVa) < 0x10000)
                    {
                        pendingHandler = uimm;
                    }
                    else if (ins.HasModRm && ins.Mod != 3 && ins.Imm is > 0 and < 1000)
                    {
                        pendingSkill = ins.Imm;
                    }
                }
                else if (ins.Kind == X86OpKind.Call && pendingHandler != 0 && pendingSkill > 0)
                {
                    // 检查这轮 mapInsert 是否紧接在前置 `pendingHandler`+`pendingSkill` 之后
                    if (ins.HasRelTarget && pe.IsTextVa(ins.RelTarget) && Math.Abs((long)ins.RelTarget - cls.DelegateInitVa) > 0x10000)
                    {
                        List<int> sts = TraceSetStates(pe, pendingHandler, pendingHandler, pendingHandler + 0x900, 0, cls, classes[0], allowCallDescent: false);
                        if (!analysis.ClassEntries.TryGetValue(cls.VtableVa, out var map))
                        {
                            map = new Dictionary<int, List<EntryPoint>>();
                            analysis.ClassEntries[cls.VtableVa] = map;
                        }
                        if (!map.TryGetValue(pendingSkill, out List<EntryPoint>? pts))
                        {
                            pts = new List<EntryPoint>();
                            map[pendingSkill] = pts;
                        }
                        if (sts.Count == 0)
                        {
                            sts.Add(-1);
                        }
                        foreach (int st in sts)
                        {
                            var ep = new EntryPoint(st, EntryKind.Dispatch);
                            if (!pts.Contains(ep))
                            {
                                pts.Add(ep);
                            }
                        }
                        pendingHandler = 0;
                        pendingSkill = 0;
                    }
                }
                pos += ins.Length;
            }
        }
    }

    private static int FindAlignedPreStart(byte[] bytes, int textStart, int textEnd, int callOff, uint callVa, uint textStartVa)
    {
        for (int back = 180; back >= 8; back--)
        {
            int cand = callOff - back;
            if (cand < textStart)
            {
                continue;
            }
            int p = cand;
            bool hit = false;
            while (p <= callOff && p < textEnd)
            {
                uint curVa = textStartVa + (uint)(p - textStart);
                if (!TryDecodeX86(bytes, p, textEnd, curVa, out X86Insn ins))
                {
                    break;
                }
                if (p == callOff)
                {
                    hit = true;
                    break;
                }
                p += ins.Length;
            }
            if (hit)
            {
                return cand;
            }
        }
        return callOff;
    }

    private static bool IsJeOpcode(in X86Insn ins) => (ins.Opcode == 0x74) || (ins.Opcode == 0x0F && ins.Opcode2 == 0x84);
    private static bool IsJneOpcode(in X86Insn ins) => (ins.Opcode == 0x75) || (ins.Opcode == 0x0F && ins.Opcode2 == 0x85);

    private static void AnalyzeSquirrel(PvfArchive pvf, int[] jobIds, NativeAnalysis analysis)
    {
        if (!TryReadText(pvf, "sqr/loadstate.nut", out string? loadStateTxt))
        {
            return;
        }
        var globalConsts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (TryReadText(pvf, "sqr/dnf_enum_header.nut", out string? enumHdr))
        {
            ParseSquirrelConsts(enumHdr!, globalConsts);
        }

        // 若有条件子树构造了专属类(仅属于某个 jobId)，读取其中的 pushState 注册
        foreach (Match m in Regex.Matches(loadStateTxt!, @"sq_RunScript\(""([^""]+)""\)"))
        {
            string rel = m.Groups[1].Value;
            string full = ("sqr/" + rel).ToLowerInvariant();
            if (!TryReadText(pvf, full, out string? txt))
            {
                continue;
            }
            var localConsts = new Dictionary<string, int>(globalConsts, StringComparer.Ordinal);
            foreach (Match pm in Regex.Matches(txt!, @"pushScriptFiles\(""([^""]+)""\)"))
            {
                string hdrPath = ("sqr/" + pm.Groups[1].Value).ToLowerInvariant();
                if (TryReadText(pvf, hdrPath, out string? hdrTxt))
                {
                    ParseSquirrelConsts(hdrTxt!, localConsts);
                }
            }

            foreach (Match sm in Regex.Matches(txt!, @"([A-Za-z0-9_]+)\.pushState\s*\(([^)]+)\)"))
            {
                string obj = sm.Groups[1].Value;
                string[] args = sm.Groups[2].Value.Split(',').Select(a => a.Trim()).ToArray();
                int targetJob = -1;
                int stateId = -1;
                int skillId = -1;

                if (args.Length >= 5 && string.Equals(obj, "IRDSQRCharacter", StringComparison.Ordinal))
                {
                    if (!TryResolveInt(args[0], localConsts, out targetJob) ||
                        !TryResolveInt(args[3], localConsts, out stateId) ||
                        !TryResolveInt(args[4], localConsts, out skillId))
                    {
                        continue;
                    }
                }
                else if (args.Length >= 4)
                {
                    if (!TryResolveInt(args[2], localConsts, out stateId) ||
                        !TryResolveInt(args[3], localConsts, out skillId))
                    {
                        continue;
                    }
                    // 对自定义子类对象的 pushState，匹配唯一拥有条件多虚表分支的职业(或按其所在脚本名所属分支匹配)
                    targetJob = InferJobForCustomSquirrelObject(pvf, jobIds, full, analysis);
                }

                if (targetJob < 0 || skillId <= 0 || stateId < 0)
                {
                    continue;
                }
                uint sqKey = uint.MaxValue - (uint)targetJob;
                if (!analysis.ClassEntries.TryGetValue(sqKey, out var sqMap))
                {
                    sqMap = new Dictionary<int, List<EntryPoint>>();
                    analysis.ClassEntries[sqKey] = sqMap;
                }
                if (!sqMap.TryGetValue(skillId, out List<EntryPoint>? pts))
                {
                    pts = new List<EntryPoint>();
                    sqMap[skillId] = pts;
                }
                var ep = new EntryPoint(stateId, EntryKind.Squirrel);
                if (!pts.Contains(ep))
                {
                    pts.Add(ep);
                }
                if (!analysis.SquirrelEntries.TryGetValue(targetJob, out List<EntryPoint>? sqAll))
                {
                    sqAll = new List<EntryPoint>();
                    analysis.SquirrelEntries[targetJob] = sqAll;
                }
                if (!sqAll.Contains(ep))
                {
                    sqAll.Add(ep);
                }
            }
        }
    }

    private static int InferJobForCustomSquirrelObject(PvfArchive pvf, int[] jobIds, string scriptPath, NativeAnalysis analysis)
    {
        // 只从 Script.pvf 的 .chr [growtype name] 列表与工厂多虚表交叉匹配，不在代码里写任何真实名字
        var data = new GameData(new PvfScriptSource(pvf));
        foreach (int jid in jobIds)
        {
            if (analysis.JobToVtables.TryGetValue(jid, out List<uint>? vts) && vts.Count >= 4)
            {
                CharacterDef? chr = data.GetCharacter(jid);
                if (chr == null)
                {
                    continue;
                }
                string[] grows = chr.Tags.Strings("[growtype name]");
                foreach (string g in grows)
                {
                    string token = Regex.Replace(g, "[^A-Za-z]", string.Empty).ToLowerInvariant();
                    if (token.Length >= 4 && scriptPath.Contains(token, StringComparison.Ordinal))
                    {
                        return jid;
                    }
                }
            }
        }
        // 优先匹配工厂中唯一存在多构造分支(即同职业内部另立子类)的职业
        if (analysis.ConditionalBranchJobs.Count == 1)
        {
            return analysis.ConditionalBranchJobs.First();
        }
        List<int> multiJobs = jobIds.Where(j => analysis.JobToVtables.TryGetValue(j, out var v) && v.Count >= 4).ToList();
        return multiJobs.Count == 1 ? multiJobs[0] : -1;
    }

    private static void ParseSquirrelConsts(string text, Dictionary<string, int> into)
    {
        foreach (string rawLine in text.Split('\n'))
        {
            int cIdx = rawLine.IndexOf("//", StringComparison.Ordinal);
            string line = (cIdx >= 0 ? rawLine.Substring(0, cIdx) : rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            Match m = Regex.Match(line, @"^(?:local\s+|const\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*(?:<-|=)\s*(-?0x[0-9a-fA-F]+|-?\d+)\b");
            if (m.Success && TryParseIntLiteral(m.Groups[2].Value, out int val))
            {
                into[m.Groups[1].Value] = val;
            }
        }
    }

    private static bool TryResolveInt(string token, Dictionary<string, int> consts, out int val)
    {
        if (TryParseIntLiteral(token, out val))
        {
            return true;
        }
        return consts.TryGetValue(token, out val);
    }

    internal static bool TryParseIntLiteral(string s, out int val)
    {
        s = s.Trim();
        if (s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(s.Substring(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u))
            {
                val = -u;
                return true;
            }
        }
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val);
        }
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out val);
    }

    private static bool TryReadText(PvfArchive pvf, string path, out string? text)
    {
        if (pvf.TryGetEntry(path, out PvfEntry entry))
        {
            text = Encoding.UTF8.GetString(pvf.Read(entry));
            return true;
        }
        text = null;
        return false;
    }
}
