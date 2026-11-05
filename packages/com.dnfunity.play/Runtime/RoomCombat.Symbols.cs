using Dnf.Game.Act;

namespace Dnf.Play
{
    // M107 怪物线 A:[SET PARTY SYMBOL] / [CHECK PARTY SYMBOL] 的队伍符号。
    public sealed partial class RoomCombatSession
    {
        private sealed partial class ActWorld
        {
            /// <summary>
            /// 队伍符号(客户端队伍对象 +0x30,观察):队伍对象复位时写 -1,所以出生值 -1 是观察。这里随房间会话(每次 AttachActs)新建,
            /// 跨房间不保留;客户端的队伍对象跨房间是否保留没有验证(未知),按"每个房间重新开始"取,与 .act 里 "== -1 才进入" 的写法相容。
            /// </summary>
            private int _partySymbol = -1;

            public int PartySymbol => _partySymbol;

            public void SetPartySymbol(int value)
            {
                _partySymbol = value;
            }
        }
    }
}
