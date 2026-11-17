namespace Dnf.Net.Tests;

/// <summary>
/// <c>MultiRoomTestKit.cs</c>(联网层测试的多房间假服务端与合成帧,编译进本程序集)里 <c>RoomKit.Wire</c> 引用了 <c>HostTestKit.RealEnvelope</c>;
/// 真的 <c>HostTestKit.cs</c> 要用联网层的内部类型(<c>OnlineTownWindows</c>),编译不进这里。这里只留签名:本程序集的测试不走"真信封"那条路
/// (宿主接线测试直接给会话假实现灌明文帧),谁误用了就明说,不悄悄给出错的字节。
/// </summary>
internal static class HostTestKit
{
    public static byte[] RealEnvelope(byte cls, ushort id, byte[] payload)
    {
        throw new NotSupportedException("Dnf.Client.Tests 里没有真信封;需要时请在联网层的测试里用 HostTestKit.RealEnvelope");
    }
}
