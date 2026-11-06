using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Session;

namespace Dnf.Net.Tests;

/// <summary>
/// 登录请求体(CMD 1/1):字段顺序与长度上限按服务端分发函数的读取顺序(E-298)逐项核对——
/// 用 <see cref="PacketReader"/> 按同样的顺序读回来,值要一致而且正好读完;每个字段越界都要在本地被拦下,
/// 因为服务端遇到这些情形只断开、不回包,发出去就无从知道错在哪。
/// </summary>
public class LoginRequestTests
{
    private static LoginRequest Valid()
    {
        return new LoginRequest
        {
            PatchTime = 3600,
            Token = Encoding.ASCII.GetBytes(new string('A', 344)),
            UserName = Encoding.ASCII.GetBytes("tester"),
            PasswordBlock = Array.Empty<byte>(),
            LoginMode = 1,
            MouseCipher = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            DataVersion = 12345,
            ServerGroup = 3,
            LocalAddress = 0x0100007Fu,
        };
    }

    [Fact]
    public void TheBodyRoundTripsThroughTheServerSideReadOrder()
    {
        LoginRequest request = Valid();
        byte[] body = request.Build();
        var reader = new PacketReader(body);

        Assert.True(reader.TryGetInt(out int patchTime));
        Assert.Equal(3600, patchTime);
        Assert.True(reader.TryGetInt(out int tokenLength));
        Assert.Equal(344, tokenLength);
        Assert.True(reader.TryGetBinary(tokenLength, out byte[] token));
        Assert.Equal(request.Token, token);
        Assert.True(reader.TryGetInt(out int userLength));
        Assert.True(reader.TryGetBinary(userLength, out byte[] user));
        Assert.Equal(request.UserName, user);
        Assert.True(reader.TryGetInt(out int passwordLength));
        Assert.Equal(0, passwordLength);                                   // 空口令块只写长度,不写字节
        Assert.True(reader.TryGetByte(out byte mode));
        Assert.Equal(1, mode);
        Assert.True(reader.TryGetInt(out int mouseLength));
        Assert.Equal(LoginRequest.MouseCipherBytes, mouseLength);
        Assert.True(reader.TryGetBinary(mouseLength, out byte[] mouse));
        Assert.Equal(request.MouseCipher, mouse);
        Assert.True(reader.TryGetInt(out int version));
        Assert.Equal(12345, version);
        Assert.True(reader.TryGetShort(out short group));
        Assert.Equal(3, group);
        Assert.True(reader.TryGetInt(out int local));
        Assert.Equal(0x0100007F, local);
        Assert.Equal(reader.Length, reader.Position);                      // 没有多写也没有少写
    }

    [Fact]
    public void TheOptionalLogoutByteIsWrittenOnlyWhenSet()
    {
        LoginRequest request = Valid();
        int without = request.Build().Length;
        request.LogoutState = 7;
        byte[] body = request.Build();
        Assert.Equal(without + 1, body.Length);
        Assert.Equal(7, body[body.Length - 1]);
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("token-zero")]
    [InlineData("token-long")]
    [InlineData("user-long")]
    [InlineData("password-long")]
    [InlineData("mode")]
    [InlineData("mouse")]
    [InlineData("group")]
    public void OutOfRangeFieldsAreRejectedLocally(string which)
    {
        LoginRequest request = Valid();
        LoginRequestProblem expected;
        switch (which)
        {
            case "patch":
                request.PatchTime = LoginRequest.MaxPatchTime + 1;
                expected = LoginRequestProblem.PatchTimeTooLarge;
                break;
            case "token-zero":
                request.Token = new byte[] { 65, 0, 66 };                  // 服务端按字符串读,0 字节会让后面的字段错位
                expected = LoginRequestProblem.BadToken;
                break;
            case "token-long":
                request.Token = new byte[LoginRequest.MaxTokenBytes + 1];
                expected = LoginRequestProblem.BadToken;
                break;
            case "user-long":
                request.UserName = new byte[LoginRequest.MaxUserNameBytes + 1];
                expected = LoginRequestProblem.BadUserName;
                break;
            case "password-long":
                request.PasswordBlock = new byte[LoginRequest.MaxPasswordBlockBytes + 1];
                expected = LoginRequestProblem.BadPasswordBlock;
                break;
            case "mode":
                request.LoginMode = LoginRequest.MaxLoginMode + 1;
                expected = LoginRequestProblem.BadLoginMode;
                break;
            case "mouse":
                request.MouseCipher = new byte[LoginRequest.MouseCipherBytes - 1];
                expected = LoginRequestProblem.BadMouseCipher;
                break;
            default:
                request.ServerGroup = LoginRequest.MaxServerGroup + 1;
                expected = LoginRequestProblem.BadServerGroup;
                break;
        }
        Assert.Equal(expected, request.Validate());
        Assert.Throws<InvalidOperationException>(() => request.Build());
    }

    [Fact]
    public void BoundaryValuesAreAccepted()
    {
        LoginRequest request = Valid();
        request.PatchTime = LoginRequest.MaxPatchTime;
        request.Token = Encoding.ASCII.GetBytes(new string('B', LoginRequest.MaxTokenBytes));
        request.UserName = Encoding.ASCII.GetBytes(new string('u', LoginRequest.MaxUserNameBytes));
        request.PasswordBlock = Encoding.ASCII.GetBytes(new string('p', LoginRequest.MaxPasswordBlockBytes));
        request.LoginMode = LoginRequest.MaxLoginMode;
        request.ServerGroup = LoginRequest.MaxServerGroup;
        Assert.Equal(LoginRequestProblem.None, request.Validate());
    }
}
