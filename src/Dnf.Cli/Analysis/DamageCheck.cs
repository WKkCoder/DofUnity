using System.Globalization;
using Dnf.Data;
using Dnf.Game;

namespace Dnf.Cli.Analysis;

/// <summary>
/// `dnf damage-check`(证据 E-049):
///   不带 --cases:用用户自备的 Script.pvf,按 C# 加载器读 Etc/EtcParameter.etc 的 [damage fomula constant] 与
///     Creature/Common/creatureCommon.cre,只输出个数与是否完整(不输出任何原版数值);
///   带 --cases &lt;文件&gt;:读 `python3 tools/py/damage_ref.py --gen` 生成的随机用例(不含原版数据),
///     用 C# 的 DamageFormula 逐个复算,与 Python 参考实现的结果、暴击标志、额外命中量逐项比较。
/// </summary>
internal static class DamageCheck
{
    public static int RunConstants(DnfLocation loc, Options opts)
    {
        using PvfArchive pvf = PvfArchive.Open(loc.PvfPath);
        var strings = new PvfStringTable(pvf.Read("stringtable.bin"));
        var constants = new DamageConstants();

        ScriptCursor Cursor(string path)
        {
            byte[] raw = pvf.Read(path);
            if (!TokenScript.IsTokenScript(raw))
            {
                throw new UserError(path + " 不是标记流脚本");
            }
            return new ScriptCursor(TokenScript.Parse(raw), strings);
        }

        DamageConstantsLoadReport etc = constants.LoadEtcParameter(Cursor("Etc/EtcParameter.etc"));
        int nonZero = 0;
        foreach (float v in FormulaValues(constants))
        {
            nonZero += v != 0f ? 1 : 0;
        }
        Console.WriteLine("EtcParameter.etc: [damage fomula constant] " + (etc.Complete ? "找到" : "没有找到")
            + ";26 项里非零 " + nonZero + " 项,值位置读到别的标记 " + etc.BadValues + " 次");

        DamageConstantsLoadReport cre = constants.LoadCreatureCommon(Cursor("Creature/Common/creatureCommon.cre"));
        Console.WriteLine("creatureCommon.cre: " + (cre.Complete ? "加载完整" : "中止于未知标签 " + cre.UnknownTag)
            + ";[damage] " + constants.CreatureDamageVector.Count + " 项(公式需要至少 7 项);值位置读到别的标记 " + cre.BadValues + " 次");
        Console.WriteLine("  召唤物玩法模块倍率 / 序号折减是否有登记: 模块 9 " + (constants.CreatureModuleRate(9) != 1.0f ? "有" : "无")
            + ",序号 0..70 里有登记的 " + Enumerable.Range(0, 71).Count(i => constants.CreatureIndexRate(i) != 1.0f) + " 个");
        return etc.Complete && cre.Complete ? 0 : 1;
    }

    public static int RunCases(Options opts)
    {
        string path = opts.CasesFile!;
        int total = 0, mismatches = 0, crits = 0, bonuses = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] f = line.Split(' ');
            if (f.Length != 10 || f[0] != "case")
            {
                throw new UserError("用例格式不对: " + line[..Math.Min(60, line.Length)]);
            }
            int id = int.Parse(f[1], CultureInfo.InvariantCulture);
            int expectResult = int.Parse(f[2], CultureInfo.InvariantCulture);
            bool expectCrit = f[3] == "1";
            int expectBonus = int.Parse(f[4], CultureInfo.InvariantCulture);
            DamageConstants k = ParseConstants(f[5], f[6], f[7], f[8]);
            byte[] blob = Convert.FromHexString(f[9]);
            DamageVariables p = DamageVariables.FromBytes(blob, blob.Length);

            int result = DamageFormula.Compute(p, k);
            total++;
            crits += p.IsCritical ? 1 : 0;
            bonuses += p.BonusHitDamage != 0 ? 1 : 0;
            if (result != expectResult || p.IsCritical != expectCrit || p.BonusHitDamage != expectBonus || p.Result != result)
            {
                mismatches++;
                if (mismatches <= 20)
                {
                    Console.WriteLine("不一致 case " + id + ": 结果 " + result + " / 期望 " + expectResult + ",暴击 " + p.IsCritical + " / " + expectCrit
                        + ",额外命中 " + p.BonusHitDamage + " / " + expectBonus);
                }
            }
        }
        Console.WriteLine("对拍 " + total + " 个用例(其中暴击 " + crits + "、额外命中 " + bonuses + "):" + (mismatches == 0 ? "全部一致" : "不一致 " + mismatches + " 个"));
        return mismatches == 0 ? 0 : 1;
    }

    private static float Bits(string hex)
    {
        return BitConverter.Int32BitsToSingle(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static IEnumerable<float> FormulaValues(DamageConstants k)
    {
        yield return k.PhysicalDamageRate;
        yield return k.PhysicalDamageAdd;
        yield return k.PhysicalReduceRate;
        yield return k.PhysicalReduceAdd;
        yield return k.PhysicalRandomBase;
        yield return k.PhysicalRandomRate;
        yield return k.MagicalDamageRate;
        yield return k.MagicalDamageAdd;
        yield return k.MagicalReduceRate;
        yield return k.MagicalReduceAdd;
        yield return k.MagicalRandomBase;
        yield return k.MagicalRandomRate;
        yield return k.CommonElementBase;
        yield return k.CommonElementRate;
        yield return k.CommonElementRateInPvp;
        yield return k.CommonElementRateInLadderPvp;
        yield return k.HpIncreaseRate;
        yield return k.HpRegenRate;
        yield return k.MpIncreaseRate;
        yield return k.MpRegenRate;
        yield return k.ActiveStatusDamageRate;
        yield return k.ActiveStatusDefenseRate;
        yield return k.PhysicalDefenseAdd;
        yield return k.MagicalDefenseAdd;
        yield return k.HpRegenRateAddRate;
        yield return k.MpRegenRateAddRate;
    }

    private static DamageConstants ParseConstants(string consts, string vector, string moduleRates, string indexRates)
    {
        string[] c = consts.Split(',');
        if (c.Length != 28)
        {
            throw new UserError("常量列应有 28 项,实际 " + c.Length);
        }
        var k = new DamageConstants
        {
            PhysicalDamageRate = Bits(c[0]),
            PhysicalDamageAdd = Bits(c[1]),
            PhysicalReduceRate = Bits(c[2]),
            PhysicalReduceAdd = Bits(c[3]),
            PhysicalRandomBase = Bits(c[4]),
            PhysicalRandomRate = Bits(c[5]),
            MagicalDamageRate = Bits(c[6]),
            MagicalDamageAdd = Bits(c[7]),
            MagicalReduceRate = Bits(c[8]),
            MagicalReduceAdd = Bits(c[9]),
            MagicalRandomBase = Bits(c[10]),
            MagicalRandomRate = Bits(c[11]),
            CommonElementBase = Bits(c[12]),
            CommonElementRate = Bits(c[13]),
            CommonElementRateInPvp = Bits(c[14]),
            CommonElementRateInLadderPvp = Bits(c[15]),
            HpIncreaseRate = Bits(c[16]),
            HpRegenRate = Bits(c[17]),
            MpIncreaseRate = Bits(c[18]),
            MpRegenRate = Bits(c[19]),
            ActiveStatusDamageRate = Bits(c[20]),
            ActiveStatusDefenseRate = Bits(c[21]),
            PhysicalDefenseAdd = Bits(c[22]),
            MagicalDefenseAdd = Bits(c[23]),
            HpRegenRateAddRate = Bits(c[24]),
            MpRegenRateAddRate = Bits(c[25]),
            MaxTotalDefenseRate = Bits(c[26]),
            CriticalDamageDefaultRate = Bits(c[27]),
        };
        k.SetCreatureDamageVector(vector == "-" ? Array.Empty<int>() : vector.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray());
        if (moduleRates != "-")
        {
            foreach (string item in moduleRates.Split(','))
            {
                string[] kv = item.Split(':');
                k.AddCreatureModuleRate(int.Parse(kv[0], CultureInfo.InvariantCulture), Bits(kv[1]));
            }
        }
        if (indexRates != "-")
        {
            foreach (string item in indexRates.Split(','))
            {
                string[] kv = item.Split(':');
                k.SetCreatureIndexRate(int.Parse(kv[0], CultureInfo.InvariantCulture), Bits(kv[1]));
            }
        }
        return k;
    }
}
