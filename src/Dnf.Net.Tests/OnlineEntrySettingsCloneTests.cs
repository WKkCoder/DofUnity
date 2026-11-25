using System.Reflection;
using Dnf.Net.Gateway;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// <see cref="OnlineEntrySettings.CloneWithoutCredentials"/>:宿主存"模板"、登录时才填凭据用的复制。
/// E-441 给 <see cref="OnlineEntrySettings"/> 加了客户端版本号,两个宿主各自抄的字段表却都漏了它,版本号在到达会话之前就丢了(E-453)。
/// 这里用反射逐个公开设置项检查:新增任何设置项而复制里没带上,这个测试就失败,不会再漏。
/// </summary>
public sealed class OnlineEntrySettingsCloneTests
{
    private static GatewayOptions Gateway()
    {
        Assert.True(GatewayOptions.TryCreate("http://localhost", GatewayTestSupport.HexLower(GatewayTestSupport.SynthKey()), TimeSpan.FromSeconds(2), out GatewayOptions options, out _));
        return options;
    }

    private static PropertyInfo[] Settings()
    {
        return typeof(OnlineEntrySettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
    }

    // 给每个公开设置项一个与新建对象缺省值不同、彼此也不同的值;遇到不认识的类型就明说要补这里
    private static object NonDefaultValue(PropertyInfo property, int index)
    {
        Type type = property.PropertyType;
        if (type == typeof(string)) return "synthetic-" + property.Name;
        if (type == typeof(byte[])) return new byte[] { 1, 2, 3, 4, 5, 6, 7, (byte)(8 + index) };
        if (type == typeof(uint)) return 0x0A0B0C00u + (uint)index;
        if (type == typeof(int)) return 4000 + index;
        if (type == typeof(ushort)) return (ushort)(7 + index);
        if (type == typeof(GatewayOptions)) return Gateway();
        throw new Xunit.Sdk.XunitException("OnlineEntrySettings 新增了类型为 " + type.Name + " 的公开设置项 " + property.Name
            + ":请在 NonDefaultValue 里补一个取值,并在 CloneWithoutCredentials 里复制它");
    }

    private static OnlineEntrySettings Populated()
    {
        var settings = new OnlineEntrySettings();
        PropertyInfo[] properties = Settings();
        for (int i = 0; i < properties.Length; i++)
        {
            properties[i].SetValue(settings, NonDefaultValue(properties[i], i));
        }
        return settings;
    }

    [Fact]
    public void Every_public_setting_is_covered_by_the_fixture_and_differs_from_its_default()
    {
        var fresh = new OnlineEntrySettings();
        var populated = Populated();
        foreach (PropertyInfo property in Settings())
        {
            object value = property.GetValue(populated);
            object defaultValue = property.GetValue(fresh);
            if (property.PropertyType == typeof(byte[]))
            {
                Assert.NotEqual((byte[])defaultValue ?? Array.Empty<byte>(), (byte[])value);
            }
            else
            {
                Assert.NotEqual(defaultValue, value);
            }
        }
        // 夹具自己不能是空的:至少有网关、账号、口令、鼠标口令密文、数据版本、频道端口、槽数、服务器组、客户端版本号这九项
        Assert.True(Settings().Length >= 9, "公开设置项的数量:" + Settings().Length);
        Assert.Contains(Settings(), p => p.Name == nameof(OnlineEntrySettings.ClientVersion));
    }

    [Fact]
    public void Every_public_setting_except_the_two_credentials_is_copied_and_the_credentials_come_out_blank()
    {
        OnlineEntrySettings source = Populated();

        OnlineEntrySettings copy = source.CloneWithoutCredentials();

        Assert.NotSame(source, copy);
        foreach (PropertyInfo property in Settings())
        {
            object original = property.GetValue(source);
            object copied = property.GetValue(copy);
            if (property.Name == nameof(OnlineEntrySettings.Account) || property.Name == nameof(OnlineEntrySettings.Password))
            {
                Assert.Equal(string.Empty, copied);
            }
            else if (property.PropertyType == typeof(byte[]))
            {
                Assert.Equal((byte[])original, (byte[])copied);
                Assert.NotSame(original, copied);                       // 鼠标口令密文是另一个数组,改它不影响来源
            }
            else if (property.PropertyType.IsValueType || property.PropertyType == typeof(string))
            {
                Assert.Equal(original, copied);
            }
            else
            {
                Assert.Same(original, copied);                          // 网关参数是不可变对象,共用同一个
            }
        }
    }

    [Fact]
    public void The_client_version_the_server_group_and_the_other_numbers_survive_the_copy_individually()
    {
        var source = new OnlineEntrySettings
        {
            Gateway = Gateway(),
            MouseCipher = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 },
            DataVersion = 123456,
            ChannelPort = 31007,
            MaxSlots = 3,
            ServerGroup = 9,
            ClientVersion = 0xDEADBEEF,
            Account = "someone",
            Password = "secret",
        };

        OnlineEntrySettings copy = source.CloneWithoutCredentials();

        Assert.Equal(0xDEADBEEFu, copy.ClientVersion);
        Assert.Equal((ushort)9, copy.ServerGroup);
        Assert.Equal(123456u, copy.DataVersion);
        Assert.Equal(31007, copy.ChannelPort);
        Assert.Equal(3, copy.MaxSlots);
        Assert.Same(source.Gateway, copy.Gateway);
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, copy.MouseCipher);
        Assert.Equal(string.Empty, copy.Account);
        Assert.Equal(string.Empty, copy.Password);

        // 来源没被动
        Assert.Equal("someone", source.Account);
        Assert.Equal("secret", source.Password);
    }

    [Fact]
    public void A_missing_mouse_cipher_stays_missing_and_changing_the_copy_never_touches_the_source()
    {
        var source = new OnlineEntrySettings { ClientVersion = 5 };
        OnlineEntrySettings copy = source.CloneWithoutCredentials();
        Assert.Null(copy.MouseCipher);
        Assert.Equal(5u, copy.ClientVersion);

        copy.ClientVersion = 6;
        copy.DataVersion = 7;
        Assert.Equal(5u, source.ClientVersion);
        Assert.Equal(0u, source.DataVersion);

        var withCipher = new OnlineEntrySettings { MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } };
        OnlineEntrySettings second = withCipher.CloneWithoutCredentials();
        second.MouseCipher[0] = 99;
        Assert.Equal((byte)1, withCipher.MouseCipher[0]);
    }

    [Fact]
    public void A_defaulted_source_copies_to_a_defaulted_copy()
    {
        var copy = new OnlineEntrySettings().CloneWithoutCredentials();
        var fresh = new OnlineEntrySettings();
        foreach (PropertyInfo property in Settings())
        {
            object expected = property.GetValue(fresh);
            object actual = property.GetValue(copy);
            if (property.PropertyType == typeof(byte[]))
            {
                Assert.Equal((byte[])expected, (byte[])actual);
            }
            else
            {
                Assert.Equal(expected, actual);
            }
        }
        Assert.Equal(30011, copy.ChannelPort);
        Assert.Equal(6, copy.MaxSlots);
        Assert.Equal(0u, copy.ClientVersion);
    }
}
