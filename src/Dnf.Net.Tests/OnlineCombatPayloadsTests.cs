using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 战斗两条命令的载荷拼装与两个服务端载荷的读法(<see cref="OnlineCombatPayloads"/>)。
/// 读回时一律用独立的手写读法(<see cref="KillView"/>、<see cref="SettlementView"/>,照笔记的布局表,不复用产品代码),
/// 免得拼与读两边同错互相掩盖。怪编号全是合成值(9100 起),没有任何真实编号。
/// </summary>
public class OnlineCombatPayloadsTests
{
    [Fact]
    public void The_default_kill_payload_is_the_54_byte_base_with_the_mob_number_first()
    {
        byte[] payload = OnlineCombatPayloads.BuildKill(9101);

        Assert.Equal(54, payload.Length);
        Assert.Equal(OnlineCombatPayloads.KillBaseLength, payload.Length);
        Assert.Equal(0x8D, payload[0]);     // 9101 = 0x238D,小端
        Assert.Equal(0x23, payload[1]);
        Assert.Equal(0xFF, payload[2]);     // 没有击杀者
        Assert.Equal(0xFF, payload[3]);

        KillView view = KillView.Parse(payload);
        Assert.Equal(9101, view.MobId);
        Assert.Equal(0xFFFF, view.Killer);
        Assert.All(view.Secure, v => Assert.Equal(0, v));
        Assert.Equal(0, view.AttackCount);
        Assert.Equal(21, view.Q);
        Assert.Equal(0, view.X);
        Assert.Equal(0, view.Y);
        Assert.Equal(new byte[] { 0, 0 }, view.Flags45);
        Assert.Equal(0, view.Passive);
        Assert.All(view.TailAfterPassive, b => Assert.Equal(0, b));
        Assert.Equal(26, view.TailAfterPassive.Length);
    }

    /// <summary>怪编号写在第 0、1 字节,小端,取遍高低字节与边界。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(9100)]
    [InlineData(9199)]
    [InlineData(59999)]
    [InlineData(65535)]
    public void The_mob_number_is_written_little_endian_in_the_first_two_bytes(int mobId)
    {
        byte[] payload = OnlineCombatPayloads.BuildKill((ushort)mobId);

        Assert.Equal((byte)mobId, payload[0]);
        Assert.Equal((byte)(mobId >> 8), payload[1]);
        Assert.Equal(mobId, KillView.Parse(payload).MobId);
        // 别的位置都不带编号:除了击杀者两个 0xFF,其余全是零
        Assert.All(payload.Skip(4), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Attack_records_are_laid_out_ten_bytes_each_and_shift_the_tail()
    {
        var attacks = new[]
        {
            new OnlineCombatAttack(0x111, 901, 12345, 3),
            new OnlineCombatAttack(0x111, 902, -7, 1),
            new OnlineCombatAttack(0x222, 903, int.MaxValue, 65535),
        };

        byte[] payload = OnlineCombatPayloads.BuildKill(9105, 901, attacks, 777, 888);

        Assert.Equal(54 + 10 * 3, payload.Length);
        KillView view = KillView.Parse(payload);
        Assert.Equal(9105, view.MobId);
        Assert.Equal(901, view.Killer);
        Assert.Equal(3, view.AttackCount);
        Assert.Equal(51, view.Q);
        for (int i = 0; i < attacks.Length; i++)
        {
            Assert.Equal(attacks[i].AttackerType, view.Attacks[i].Type);
            Assert.Equal(attacks[i].AttackerId, view.Attacks[i].Id);
            Assert.Equal(attacks[i].Damage, view.Attacks[i].Damage);
            Assert.Equal(attacks[i].Hits, view.Attacks[i].Hits);
        }
        Assert.Equal(777, view.X);
        Assert.Equal(888, view.Y);
        Assert.Equal(0, view.Passive);
        Assert.All(view.TailAfterPassive, b => Assert.Equal(0, b));
    }

    /// <summary>被动物件标志(攻击记录之后第 6 字节)任何条数下都是 0:怪物是 1 的话服务端会走销毁被动物件的分支且不回包。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(254)]
    public void The_passive_object_flag_is_never_set(int attackCount)
    {
        var attacks = Enumerable.Range(0, attackCount).Select(i => new OnlineCombatAttack(0x111, (ushort)(900 + i), 100 + i, 1)).ToArray();

        byte[] payload = OnlineCombatPayloads.BuildKill(9100, 901, attacks);

        Assert.Equal(54 + 10 * attackCount, payload.Length);
        KillView view = KillView.Parse(payload);
        Assert.Equal(attackCount, view.AttackCount);
        Assert.Equal(0, view.Passive);
        Assert.Equal(0, payload[view.Q + 6]);
    }

    [Fact]
    public void More_than_254_attack_records_are_refused()
    {
        var most = Enumerable.Range(0, OnlineCombatPayloads.MaxAttackRecords).Select(i => new OnlineCombatAttack(0x111, 900, 1, 1)).ToArray();
        Assert.Equal(54 + 10 * 254, OnlineCombatPayloads.BuildKill(9100, 901, most).Length);

        var tooMany = Enumerable.Range(0, OnlineCombatPayloads.MaxAttackRecords + 1).Select(i => new OnlineCombatAttack(0x111, 900, 1, 1)).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => OnlineCombatPayloads.BuildKill(9100, 901, tooMany));
    }

    [Fact]
    public void The_settlement_payload_is_33_zero_bytes_that_the_server_read_order_consumes_exactly()
    {
        byte[] payload = OnlineCombatPayloads.BuildSettlement();

        Assert.Equal(33, payload.Length);
        Assert.Equal(OnlineCombatPayloads.SettlementLength, payload.Length);
        Assert.All(payload, b => Assert.Equal(0, b));
        SettlementView view = SettlementView.Parse(payload);
        Assert.Equal(0, view.N);
        Assert.Equal(33, view.Consumed);
        Assert.All(view.Fields, f => Assert.Equal(0, f));
    }

    [Fact]
    public void Every_call_returns_a_fresh_array()
    {
        byte[] first = OnlineCombatPayloads.BuildSettlement();
        first[0] = 0x55;
        first[32] = 0x66;
        Assert.All(OnlineCombatPayloads.BuildSettlement(), b => Assert.Equal(0, b));

        byte[] kill = OnlineCombatPayloads.BuildKill(9100);
        kill[0] = 0x77;
        Assert.Equal(9100, KillView.Parse(OnlineCombatPayloads.BuildKill(9100)).MobId);
    }

    // ======== 读服务端载荷 ========

    [Theory]
    [InlineData(0)]
    [InlineData(9100)]
    [InlineData(9199)]
    [InlineData(65535)]
    public void The_kill_confirmation_carries_the_mob_number_in_its_first_two_bytes(int mobId)
    {
        byte[] payload = CombatKit.ConfirmPayload(mobId);

        Assert.True(OnlineCombatPayloads.TryReadKillConfirm(payload, out ushort read));
        Assert.Equal(mobId, read);

        // 会话加密补的零不影响读
        byte[] padded = payload.Concat(new byte[10]).ToArray();
        Assert.True(OnlineCombatPayloads.TryReadKillConfirm(padded, out ushort readPadded));
        Assert.Equal(mobId, readPadded);
    }

    [Fact]
    public void A_kill_confirmation_needs_two_bytes()
    {
        Assert.False(OnlineCombatPayloads.TryReadKillConfirm(null, out ushort a));
        Assert.Equal(0, a);
        Assert.False(OnlineCombatPayloads.TryReadKillConfirm(new byte[0], out _));
        Assert.False(OnlineCombatPayloads.TryReadKillConfirm(new byte[] { 0x8D }, out ushort b));
        Assert.Equal(0, b);
        Assert.True(OnlineCombatPayloads.TryReadKillConfirm(new byte[] { 0x8D, 0x23 }, out ushort c));
        Assert.Equal(9101, c);
    }

    [Fact]
    public void The_kill_refusal_is_a_zero_flag_an_error_code_and_the_mob_number()
    {
        Assert.True(OnlineCombatPayloads.TryReadKillRefusal(CombatKit.RefusalPayload(9101, 5), out byte code, out ushort mobId));
        Assert.Equal(5, code);
        Assert.Equal(9101, mobId);

        byte[] padded = CombatKit.RefusalPayload(9102, 7).Concat(new byte[12]).ToArray();
        Assert.True(OnlineCombatPayloads.TryReadKillRefusal(padded, out byte code2, out ushort mobId2));
        Assert.Equal(7, code2);
        Assert.Equal(9102, mobId2);
    }

    /// <summary>不是失败形状的一律读不出:成功形状(首字节非 0)、太短、null。</summary>
    [Fact]
    public void Only_the_failure_shape_is_read_as_a_refusal()
    {
        Assert.False(OnlineCombatPayloads.TryReadKillRefusal(null, out byte c0, out ushort m0));
        Assert.Equal(0, c0);
        Assert.Equal(0, m0);
        Assert.False(OnlineCombatPayloads.TryReadKillRefusal(new byte[0], out _, out _));
        Assert.False(OnlineCombatPayloads.TryReadKillRefusal(new byte[] { 0, 5, 0x8D }, out _, out _));
        Assert.False(OnlineCombatPayloads.TryReadKillRefusal(new byte[] { 1, 0, 0x8D, 0x23 }, out byte c1, out ushort m1));
        Assert.Equal(0, c1);
        Assert.Equal(0, m1);
        Assert.False(OnlineCombatPayloads.TryReadKillRefusal(new byte[] { 0xFF, 5, 0x8D, 0x23 }, out _, out _));
    }
}
