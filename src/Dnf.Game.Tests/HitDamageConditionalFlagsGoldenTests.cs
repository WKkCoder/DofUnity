using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Dnf.Game;
using Xunit;

namespace Dnf.Game.Tests;

/// <summary>
/// 0x0087b870 防御者类别→三条件开关;期望值来自 Golden 手算(见文件头),非实现抄录。
/// </summary>
public sealed class HitDamageConditionalFlagsGoldenTests
{
    [Fact]
    public void GoldenTripleFlagsMatchDisasmHandCalculation()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden", "hit_damage_conditional_flags_golden.txt");
        path = Path.GetFullPath(path);
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            string[] parts = line.Split('\t');
            var ids = ParseIds(parts[0]);
            bool f0 = parts[1].Trim() == "1";
            bool f1 = parts[2].Trim() == "1";
            bool f2 = parts[3].Trim() == "1";
            Assert.Equal((f0, f1, f2), ConditionalDamageRateFlags.ComputeFlags(ids));
        }
    }

    [Fact]
    public void FillAppliesCategoryFlagsForHeroDefenderPanel()
    {
        var input = new HitInput(0f, true, AttackKind.Physic, 0f, 0, -1000f, false, 0, 80f);
        var withHuman = new List<int> { ConditionalDamageRateFlags.HumanArmorCategoryId };
        DamageVariables p = HitDamage.Fill(input, new AttackerPanel { Level = 1 }, DefenderPanel.FromCharacter(1, 0, 0, withHuman), false, 1);
        Assert.Equal((true, false, false), (p.ConditionalFlag(0), p.ConditionalFlag(1), p.ConditionalFlag(2)));
        Assert.Equal(80f, p.ConditionalPercent(0));
    }

    [Fact]
    public void FillKeepsFalseFalseTrueWhenNoArmorCategories()
    {
        var input = new HitInput(0f, true, AttackKind.Physic, 0f, 0, -1000f, false, 0, 80f);
        DamageVariables p = HitDamage.Fill(input, new AttackerPanel { Level = 1 }, DefenderPanel.FromCharacter(1, 0, 0), false, 1);
        Assert.Equal((false, false, true), (p.ConditionalFlag(0), p.ConditionalFlag(1), p.ConditionalFlag(2)));
    }

    private static List<int> ParseIds(string text)
    {
        var ids = new List<int>();
        if (text.Length == 0)
        {
            return ids;
        }
        foreach (string tok in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            ids.Add(int.Parse(tok, CultureInfo.InvariantCulture));
        }
        return ids;
    }
}
