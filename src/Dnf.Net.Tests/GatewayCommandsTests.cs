using Dnf.Net.Gateway;

namespace Dnf.Net.Tests;

/// <summary>四个命令的明文构造与字段校验。期望的明文用手写字符串;MD5 期望值来自 RFC 1321 的向量与独立工具(Python hashlib)的计算。</summary>
public sealed class GatewayCommandsTests
{
    private const string Md5Abc = "900150983cd24fb0d6963f7d28e17f72";
    private const string Md5A = "0cc175b9c0f1b6a831c399e269772661";

    // ---------- MD5 ----------

    [Theory]
    [InlineData("", "d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("a", "0cc175b9c0f1b6a831c399e269772661")]
    [InlineData("abc", "900150983cd24fb0d6963f7d28e17f72")]
    [InlineData("message digest", "f96b697d7cb7938d525a2f31aaf161d0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "c3fcd3d76192e4007dfb496cca67e13b")]
    [InlineData("Passw0rd!", "47b7bfb65fa83ac9a71dcb0f6296bb6e")]
    public void Md5HexMatchesTheReferenceDigests(string text, string expected)
    {
        Assert.Equal(expected, GatewayCommands.Md5Hex(text));
    }

    [Theory]
    [InlineData("密码", "a8105204604a0b11e916f3879aae3b0b")]
    [InlineData("pässwörd", "12841e4ba5e37d2fbfc78458c6714ade")]
    [InlineData("😀", "2a02eac39d716a70ecf37579185927b6")]
    public void Md5HexHashesTheUtf8BytesWithoutBom(string text, string expected)
    {
        // 期望值是对 UTF-8 字节算出来的(Latin-1 或 UTF-16 的摘要是另外的值,见独立计算)
        Assert.Equal(expected, GatewayCommands.Md5Hex(text));
    }

    [Fact]
    public void Md5HexIsLowercaseThirtyTwoCharactersForAnyInput()
    {
        for (int i = 0; i < 40; i++)
        {
            string digest = GatewayCommands.Md5Hex(new string('x', i));
            Assert.True(GatewayCommands.IsMd5Hex(digest), "length " + i);
        }
        Assert.Throws<ArgumentNullException>(() => GatewayCommands.Md5Hex(null));
    }

    // ---------- 命令明文 ----------

    [Fact]
    public void LoginBuildsFourPipeSeparatedFields()
    {
        Assert.Equal("login|Tester_01|" + Md5Abc + "|00:00:00:00:00:00", GatewayCommands.Login("Tester_01", Md5Abc));
        Assert.Equal("login|abcd|" + Md5A + "|11:22:33:44:55:66", GatewayCommands.Login("abcd", Md5A, "11:22:33:44:55:66"));
        Assert.Equal("login|abcd|" + Md5A + "|00:00:00:00:00:00", GatewayCommands.Login("abcd", Md5A, null));
    }

    [Fact]
    public void RegisterBuildsFourFieldsWithAnOptionalQq()
    {
        Assert.Equal("regedit|abcd|" + Md5A + "|12345678", GatewayCommands.Register("abcd", Md5A, "12345678"));
        Assert.Equal("regedit|abcd|" + Md5A + "|", GatewayCommands.Register("abcd", Md5A));
        Assert.Equal("regedit|abcd|" + Md5A + "|", GatewayCommands.Register("abcd", Md5A, string.Empty));
    }

    [Fact]
    public void ForgotPasswordPutsQqBeforeTheNewDigest()
    {
        Assert.Equal("forget|abcd|12345678|" + Md5Abc, GatewayCommands.ForgotPassword("abcd", "12345678", Md5Abc));
        Assert.Equal("forget|abcd||" + Md5Abc, GatewayCommands.ForgotPassword("abcd", null, Md5Abc));
    }

    [Fact]
    public void ChangePasswordPutsOldBeforeNew()
    {
        Assert.Equal("repasswd|abcd|" + Md5A + "|" + Md5Abc, GatewayCommands.ChangePassword("abcd", Md5A, Md5Abc));
    }

    [Fact]
    public void EveryCommandHasExactlyThreeSeparators()
    {
        string[] all =
        {
            GatewayCommands.Login("abcd", Md5A),
            GatewayCommands.Register("abcd", Md5A, "12345"),
            GatewayCommands.Register("abcd", Md5A),
            GatewayCommands.ForgotPassword("abcd", "12345", Md5A),
            GatewayCommands.ChangePassword("abcd", Md5A, Md5Abc),
        };
        Assert.All(all, text => Assert.Equal(3, text.Count(c => c == '|')));
    }

    // ---------- 用户名 ----------

    [Theory]
    [InlineData("abcd")]
    [InlineData("ABCD")]
    [InlineData("a_b_")]
    [InlineData("0123")]
    [InlineData("____")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("Z9_zA0_9Z9_zA0_9Z9_zA0_9Z9_zA0_9")]
    public void ValidUsernamesAreAccepted(string name)
    {
        Assert.True(GatewayCommands.IsValidUsername(name));
        Assert.StartsWith("login|" + name + "|", GatewayCommands.Login(name, Md5A));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    [InlineData("ab cd")]
    [InlineData("ab-cd")]
    [InlineData("ab|cd")]
    [InlineData("ab.cd")]
    [InlineData("abcd\n")]
    [InlineData(" abcd")]
    [InlineData("用户名称很长")]
    [InlineData("abcé")]
    [InlineData("ab@d")]
    [InlineData("ab/d")]
    [InlineData("ab[d")]
    [InlineData("ab`d")]
    [InlineData("ab{d")]
    [InlineData("ab:d")]
    [InlineData("ab\u00a0d")]
    public void InvalidUsernamesAreRejectedByEveryCommand(string name)
    {
        Assert.False(GatewayCommands.IsValidUsername(name));
        Assert.Throws<ArgumentException>(() => GatewayCommands.Login(name, Md5A));
        Assert.Throws<ArgumentException>(() => GatewayCommands.Register(name, Md5A));
        Assert.Throws<ArgumentException>(() => GatewayCommands.ForgotPassword(name, "12345", Md5A));
        Assert.Throws<ArgumentException>(() => GatewayCommands.ChangePassword(name, Md5A, Md5A));
    }

    [Fact]
    public void UsernameLengthBoundaryIsFourToThirtyTwo()
    {
        Assert.False(GatewayCommands.IsValidUsername(new string('a', 3)));
        Assert.True(GatewayCommands.IsValidUsername(new string('a', 4)));
        Assert.True(GatewayCommands.IsValidUsername(new string('a', 32)));
        Assert.False(GatewayCommands.IsValidUsername(new string('a', 33)));
    }

    // ---------- MD5 字段 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0cc175b9c0f1b6a831c399e26977266")]
    [InlineData("0cc175b9c0f1b6a831c399e2697726611")]
    [InlineData("0CC175B9C0F1B6A831C399E269772661")]
    [InlineData("0cc175b9c0f1b6a831c399e26977266G")]
    [InlineData("0cc175b9c0f1b6a831c399e26977266g")]
    [InlineData("0cc175b9c0f1b6a831c399e26977266 ")]
    [InlineData("0cc175b9c0f1b6a831c399e26977266|")]
    [InlineData("0cc175b9c0f1b6a831c399e26977266\u0661")]
    public void Md5FieldMustBeExactlyThirtyTwoLowercaseHexCharacters(string digest)
    {
        Assert.False(GatewayCommands.IsMd5Hex(digest));
        Assert.Throws<ArgumentException>(() => GatewayCommands.Login("abcd", digest));
        Assert.Throws<ArgumentException>(() => GatewayCommands.Register("abcd", digest));
        Assert.Throws<ArgumentException>(() => GatewayCommands.ForgotPassword("abcd", "12345", digest));
        Assert.Throws<ArgumentException>(() => GatewayCommands.ChangePassword("abcd", digest, Md5A));
        Assert.Throws<ArgumentException>(() => GatewayCommands.ChangePassword("abcd", Md5A, digest));
    }

    [Fact]
    public void Md5CharacterClassBoundariesAreExact()
    {
        // 0-9 与 a-f 的边界两侧:'/' ':' '`' 'g' '@' 'G' 都不是合法字符
        foreach (char c in "0123456789abcdef")
        {
            Assert.True(GatewayCommands.IsMd5Hex(new string(c, 32)), "accepted char " + c);
        }
        foreach (char c in "/:`g@GAF")
        {
            Assert.False(GatewayCommands.IsMd5Hex(new string(c, 32)), "rejected char " + c);
        }
        string mixed = new string('a', 31) + "G";
        Assert.False(GatewayCommands.IsMd5Hex(mixed));
        Assert.False(GatewayCommands.IsMd5Hex("G" + new string('a', 31)));
    }

    // ---------- 自由字段 ----------

    [Theory]
    [InlineData("a|b")]
    [InlineData("|")]
    [InlineData("12345\n")]
    [InlineData("12\r345")]
    [InlineData("12\t345")]
    [InlineData("12\0345")]
    [InlineData("12345\u007f")]
    [InlineData("\u001f")]
    public void FreeFieldsRejectSeparatorsAndControlCharacters(string value)
    {
        Assert.False(GatewayCommands.IsSafeFreeField(value));
        Assert.Throws<ArgumentException>(() => GatewayCommands.Register("abcd", Md5A, value));
        Assert.Throws<ArgumentException>(() => GatewayCommands.ForgotPassword("abcd", value, Md5A));
        Assert.Throws<ArgumentException>(() => GatewayCommands.Login("abcd", Md5A, value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData(" 12345 ")]
    [InlineData("中文也可以")]
    [InlineData("a b:c;d,e.f")]
    [InlineData("~")]
    [InlineData(" ")]
    public void FreeFieldsLeaveJudgementOfTheContentToTheServer(string value)
    {
        Assert.True(GatewayCommands.IsSafeFreeField(value));
        Assert.Equal("regedit|abcd|" + Md5A + "|" + value, GatewayCommands.Register("abcd", Md5A, value));
        Assert.Equal("forget|abcd|" + value + "|" + Md5A, GatewayCommands.ForgotPassword("abcd", value, Md5A));
    }

    [Fact]
    public void NullFreeFieldIsNotSafeButCommandsTreatNullAsEmpty()
    {
        Assert.False(GatewayCommands.IsSafeFreeField(null));
        Assert.Equal("regedit|abcd|" + Md5A + "|", GatewayCommands.Register("abcd", Md5A, null));
    }

    // ---------- 错误消息不回显字段值 ----------

    [Fact]
    public void ErrorMessagesNameTheRuleButNeverTheValue()
    {
        string badUser = "bad|user";
        string badDigest = "ZZZZ0cc175b9c0f1b6a831c399e2697";
        string badQq = "q|q|q";
        ArgumentException u = Assert.Throws<ArgumentException>(() => GatewayCommands.Login(badUser, Md5A));
        ArgumentException d = Assert.Throws<ArgumentException>(() => GatewayCommands.Login("abcd", badDigest));
        ArgumentException q = Assert.Throws<ArgumentException>(() => GatewayCommands.Register("abcd", Md5A, badQq));
        Assert.DoesNotContain(badUser, u.Message);
        Assert.DoesNotContain("ZZZZ", d.Message);
        Assert.DoesNotContain(badQq, q.Message);
        Assert.Contains("username", u.Message);
        Assert.Contains("password md5", d.Message);
        Assert.Contains("qq", q.Message);
    }

    [Fact]
    public void NewAndOldDigestErrorsAreDistinguishable()
    {
        ArgumentException oldBad = Assert.Throws<ArgumentException>(() => GatewayCommands.ChangePassword("abcd", "x", Md5A));
        ArgumentException newBad = Assert.Throws<ArgumentException>(() => GatewayCommands.ChangePassword("abcd", Md5A, "x"));
        Assert.Contains("old", oldBad.Message);
        Assert.DoesNotContain("old", newBad.Message);
        Assert.Contains("new", newBad.Message);
    }

    [Fact]
    public void ValidationHappensBeforeAnyPlaintextIsProduced()
    {
        // 第一个字段合法、第二个非法:不能产出半截命令
        Assert.Throws<ArgumentException>(() => GatewayCommands.ChangePassword("abcd", Md5A, new string('a', 31)));
        Assert.Throws<ArgumentException>(() => GatewayCommands.ForgotPassword("abcd", "a|b", Md5A));
    }
}
