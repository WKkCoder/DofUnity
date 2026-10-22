using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-111(服务端 importServerParameterScript 里这几个标签的存放位置,与 CParty::getMonsterTotalExp 取用的偏移逐项对过)。
namespace Dnf.Game
{
    /// <summary>
    /// 击杀经验用到的三张倍率表(etc/serverparameter.etc,证据 E-111):
    /// <c>[party user number exp bonusrate]</c>(4 个浮点,队伍 1 到 4 人)、
    /// <c>[dungeon difficulty exp bonusrate]</c>(5 个浮点,副本难度 0 到 4)、
    /// <c>[drop bonusrate of monster kind]</c>(4 个浮点,怪物种类 0 到 3:普通、小精英、大精英、首领——经验公式直接拿这张"掉落倍率表"当种类倍率)。
    /// 文件或某个标签缺失时对应的表取全 1(中性;服务端的静态默认是 0,那样一杀怪就没有经验,单机没有意义)。数值是原版数据,不入库。
    /// </summary>
    public sealed class ExpRates
    {
        public const string Path = "etc/serverparameter.etc";

        public const int PartySize = 4;
        public const int DifficultyCount = 5;
        public const int KindCount = 4;

        private readonly float[] _party;
        private readonly float[] _difficulty;
        private readonly float[] _kind;

        /// <summary>全部为 1 的中性倍率。</summary>
        public static ExpRates Neutral => new ExpRates(null, null, null);

        public ExpRates(IReadOnlyList<float> party, IReadOnlyList<float> difficulty, IReadOnlyList<float> kind)
        {
            _party = Fill(party, PartySize);
            _difficulty = Fill(difficulty, DifficultyCount);
            _kind = Fill(kind, KindCount);
        }

        private static float[] Fill(IReadOnlyList<float> source, int count)
        {
            var table = new float[count];
            for (int i = 0; i < count; i++)
            {
                table[i] = source != null && i < source.Count ? source[i] : 1f;
            }
            return table;
        }

        /// <summary>
        /// 队伍人数的经验倍率:服务端取表里第 (人数 - 1) 项,人数小于 1 取 1。人数超过 4 时服务端会读到表外(没有意义),这里按 4 人算。
        /// </summary>
        public float PartyRate(int members)
        {
            if (members < 1)
            {
                return 1f;
            }
            return _party[Math.Min(members, PartySize) - 1];
        }

        /// <summary>副本难度的经验倍率(0 = 普通……);难度越界取 1(服务端不检查,读到表外)。</summary>
        public float DifficultyRate(int difficulty)
        {
            return (uint)difficulty < (uint)DifficultyCount ? _difficulty[difficulty] : 1f;
        }

        /// <summary>怪物种类的倍率:种类 0 到 3 取表,大于 3 取 1(getMonsterTotalExp 里先判断 "种类 &gt; 3")。种类为负视为越界,取 1。</summary>
        public float KindRate(int role)
        {
            return (uint)role < (uint)KindCount ? _kind[role] : 1f;
        }

        /// <summary>读 etc/serverparameter.etc 的脚本行;<paramref name="lines"/> 为 null 返回中性倍率。数值可以写成整数或浮点。</summary>
        public static ExpRates Parse(ScriptLine[] lines)
        {
            if (lines == null)
            {
                return Neutral;
            }
            List<float> party = null, difficulty = null, kind = null;
            foreach (ScriptLine line in lines)
            {
                if (party == null && line.TagIs("[party user number exp bonusrate]"))
                {
                    party = Floats(line);
                }
                else if (difficulty == null && line.TagIs("[dungeon difficulty exp bonusrate]"))
                {
                    difficulty = Floats(line);
                }
                else if (kind == null && line.TagIs("[drop bonusrate of monster kind]"))
                {
                    kind = Floats(line);
                }
            }
            return new ExpRates(party, difficulty, kind);
        }

        private static List<float> Floats(ScriptLine line)
        {
            var list = new List<float>(line.Values.Length);
            foreach (ScriptValue v in line.Values)
            {
                if (v.IsNumber)
                {
                    list.Add(v.Int);
                }
                else if (v.Kind == ScriptValueKind.Float)
                {
                    list.Add(v.Float);
                }
            }
            return list;
        }
    }
}
