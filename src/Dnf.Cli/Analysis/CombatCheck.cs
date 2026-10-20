using System.Globalization;
using System.Text;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// combat-check:在一个副本房间里按时间表替主角按键,逐步推进战斗模拟(60 帧每秒的固定步长),
/// 打印每次命中(时刻、第几个怪物、伤害、剩余血量、击杀)与最后的状态,并可在指定时刻输出 800×600 的画面(证据 E-069)。
/// 时间表 --play 的每一项是 "秒:动作":动作是 none、left、right、up、down(可用 + 组合),再加上 attack 表示在那一刻按一下攻击键;
/// 方向保持到下一项改变为止。伤害是占位值(<see cref="PlaceholderDamage"/>),不是原版公式的结果。
/// </summary>
internal static class CombatCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        CombatScript script = ParsePlay(opts.Play);
        var shotTimes = new List<double>();
        if (opts.ShotsAt != null)
        {
            foreach (string item in opts.ShotsAt.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                shotTimes.Add(double.Parse(item, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            shotTimes.Sort();
            if (opts.PngFile == null)
            {
                throw new UserError("--shots-at 需要 --png <文件名前缀>(每个时刻写成 前缀-秒.png)");
            }
        }

        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        RoomCombatSession session = DemoCombat.CreateSession(assets, opts.MapPath, opts.HeroWeapon ?? DemoHero.StarterWeapon, 1, !opts.NoDamage);
        if (opts.Start != null)
        {
            string[] p = opts.Start.Split(',');
            if (p.Length != 3 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sx)
                || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sy) || (p[2] != "left" && p[2] != "right"))
            {
                throw new UserError("--start 需要 x,y,left|right");
            }
            session.Hero.Place(sx, sy, p[2] == "left", HeroMotion.Rest, 0);
            session.Camera.Place(sx - TownCamera.DefaultCenterX);
        }

        Console.WriteLine("房间 " + opts.MapPath + ":怪物 " + session.Monsters.Count + " 个,主角连招 " + session.Hero.AttackCount + " 段,出生 ("
            + F(session.Hero.X) + "," + F(session.Hero.Y) + ") 朝" + (session.Hero.FacingLeft ? "左" : "右") + ",镜头 x " + session.Camera.IntX
            + ",主角脚本 [move speed] " + session.Hero.MoveSpeedStat + ",武器命中停顿 " + (session.WeaponStunMs > 0 ? "有" : "无") + "(E-077)"
            + ",受击硬直 " + (session.StunTable != null ? "按客户端公式" : "占位值") + "(E-080),倒地 按客户端的开始处理与三阶段(E-083)"
            + (session.Vitals != null
                ? ",怪物还手(E-086):主角生命 " + session.Vitals.Hp + "/" + session.Vitals.HpMax + ",伤害 " + (session.HasDamageConstants ? "按原版公式(输入是推断)" : "占位")
                : ",怪物不还手(--no-damage)"));
        foreach (MonsterActor m in session.Monsters)
        {
            Console.WriteLine("  怪物 " + m.Index + ":编号 " + m.Code + (m.IsBoss ? "(首领)" : string.Empty) + " 等级 " + m.Level + " 生命 " + m.HpMax
                + " 位置 (" + F(m.X) + "," + F(m.Y) + ") 移速 " + F(m.SpeedX) + "/" + F(m.SpeedY) + " 像素每秒");
        }

        double totalMs = opts.Seconds * 1000.0;
        int shotIndex = 0;
        int lastHits = 0;
        int lastHeroHits = 0;
        double t = 0;
        string lastState = string.Empty;
        while (script.TimeMs < totalMs)
        {
            script.Step(session);
            t = script.TimeMs;

            if (session.Hits != lastHits)
            {
                var sb = new StringBuilder();
                sb.Append("  t=" + (t / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "s  命中 +" + (session.Hits - lastHits) + "  累计 " + session.Hits
                    + " 击杀 " + session.Kills + " 连击 " + session.Combo + "  第 " + (session.Hero.AttackStage + 1) + " 段"
                    + (session.HeroPauseMs > 0 ? "  定住 " + F(session.HeroPauseMs) + " 毫秒" : string.Empty));
                foreach (MonsterActor m in session.Monsters)
                {
                    if (m.Phase is MonsterPhase.Hurt or MonsterPhase.Dying)
                    {
                        sb.Append("  [怪物 " + m.Index + " 血 " + m.Hp + "/" + m.HpMax + " " + m.Phase
                            + (m.IsKnockedDown ? " 倒地阶段 " + m.DownStage + " 离地 " + F(m.Z) + " 水平速度 " + F(m.HorizontalSpeed) : string.Empty) + "]");
                    }
                }
                Console.WriteLine(sb.ToString());
                lastHits = session.Hits;
            }
            if (session.HeroHits != lastHeroHits)
            {
                Console.WriteLine("  t=" + (t / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "s  主角被打中 +" + (session.HeroHits - lastHeroHits) + "  累计 "
                    + session.HeroHits + " 次,共 " + session.HeroDamageTaken + " 点  生命 " + session.Vitals!.Hp + "/" + session.Vitals.HpMax
                    + "  受击硬直 " + F(session.Hero.HurtSpanMs) + " 毫秒" + (session.HeroDead ? "  主角死亡" : string.Empty)
                    + (session.HeroPauseMs > 0 ? "  定住 " + F(session.HeroPauseMs) + " 毫秒" : string.Empty));
                lastHeroHits = session.HeroHits;
            }
            string state = session.Hero.Motion + "/" + session.Hero.AttackStage;
            if (state != lastState)
            {
                Console.WriteLine("  t=" + (t / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "s  主角 " + session.Hero.Motion
                    + (session.Hero.IsAttacking && session.Hero.AttackKind == HeroAttackKind.Normal ? " 第 " + (session.Hero.AttackStage + 1) + " 段" : string.Empty)
                    + (session.Hero.IsAirborne ? " 离地 " + F(session.Hero.Z) : string.Empty)
                    + " (" + F(session.Hero.X) + "," + F(session.Hero.Y) + ") 朝" + (session.Hero.FacingLeft ? "左" : "右") + " 镜头 " + session.Camera.IntX);
                lastState = state;
            }
            if (opts.Trace && (long)(t / 100.0) != (long)((t - CombatScript.StepMs) / 100.0))
            {
                var tr = new StringBuilder("  [跟踪 " + (t / 1000.0).ToString("F2", CultureInfo.InvariantCulture) + "s] 主角 (" + F(session.Hero.X) + "," + F(session.Hero.Y) + ")");
                foreach (MonsterActor m in session.Monsters)
                {
                    tr.Append("  #" + m.Index + " " + m.Phase + (m.IsKnockedDown ? "/倒地" + m.DownStage : string.Empty) + " (" + F(m.X) + "," + F(m.Y) + (m.Z > 0 ? "," + F(m.Z) : string.Empty) + ") " + m.Hp);
                }
                Console.WriteLine(tr.ToString());
            }
            while (shotIndex < shotTimes.Count && shotTimes[shotIndex] * 1000.0 <= t)
            {
                WriteShot(session, shotTimes[shotIndex], opts.PngFile!);
                shotIndex++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("结束:主角 (" + F(session.Hero.X) + "," + F(session.Hero.Y) + "),命中 " + session.Hits + " 次,放技能 " + session.SkillCasts + " 次,击杀 " + session.Kills + " 个,总伤害 " + session.TotalDamage
            + ",剩余 " + session.AliveCount + " 个"
            + (session.Vitals != null
                ? ",主角被打中 " + session.HeroHits + " 次(共 " + session.HeroDamageTaken + " 点),生命 " + session.Vitals.Hp + "/" + session.Vitals.HpMax
                    + ",魔法 " + session.Vitals.Mp + "/" + session.Vitals.MpMax
                    + ",动作 " + session.Hero.Motion
                : string.Empty));
        foreach (MonsterActor m in session.Monsters)
        {
            Console.WriteLine("  怪物 " + m.Index + ":" + m.Phase + " 血 " + m.Hp + "/" + m.HpMax + " 位置 (" + F(m.X) + "," + F(m.Y) + ") 朝" + (m.FacingLeft ? "左" : "右"));
        }
        return 0;
    }

    private static void WriteShot(RoomCombatSession session, double seconds, string prefix)
    {
        var canvas = new RgbaCanvas(800, 600);
        canvas.Clear(40, 40, 40, 255);
        TownScene.Render(canvas, session.Build((long)(seconds * 1000.0)));
        string path = prefix + "-" + seconds.ToString("0.00", CultureInfo.InvariantCulture) + ".png";
        PngWriter.Write(path, 800, 600, canvas.Pixels);
        Console.WriteLine("  已写 " + path);
    }

    private static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    private static CombatScript ParsePlay(string? text)
    {
        try
        {
            return CombatScript.Parse(text ?? string.Empty);
        }
        catch (FormatException e)
        {
            throw new UserError("--play:" + e.Message);
        }
    }
}
