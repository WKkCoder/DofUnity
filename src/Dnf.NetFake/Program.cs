using System.Globalization;

namespace Dnf.NetFake;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string readyFile = null;
        string devRootDir = null;
        FakeFaultMode fault = FakeFaultMode.None;
        FakeReturnSignalMode returnSignal = FakeReturnSignalMode.AreaPair;
        int monsterCount = 2;
        int roomCount = 1;
        ushort dungeonId = 1;
        string channelName = "syn01";

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string next = i + 1 < args.Length ? args[i + 1] : null;
            switch (a)
            {
                case "--ready-file":
                    if (next == null) return Usage();
                    readyFile = next;
                    i++;
                    break;
                case "--write-dev-root":
                    if (next == null) return Usage();
                    devRootDir = next;
                    i++;
                    break;
                case "--fault":
                    if (next == null || !TryParseFault(next, out fault)) return Usage();
                    i++;
                    break;
                case "--return-signal":
                    if (next == "noti2") returnSignal = FakeReturnSignalMode.Noti2;
                    else if (next == "area") returnSignal = FakeReturnSignalMode.AreaPair;
                    else return Usage();
                    i++;
                    break;
                case "--monsters":
                    if (next == null || !int.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out monsterCount) || monsterCount < 1 || monsterCount > 16) return Usage();
                    i++;
                    break;
                case "--rooms":
                    if (next == null || !int.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out roomCount) || roomCount < 1 || roomCount > 8) return Usage();
                    i++;
                    break;
                case "--dungeon-id":
                    if (next == null || !ushort.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out dungeonId)) return Usage();
                    i++;
                    break;
                case "--channel-name":
                    if (string.IsNullOrEmpty(next)) return Usage();
                    channelName = next;
                    i++;
                    break;
                case "-h":
                case "--help":
                    Usage();
                    return 0;
                default:
                    return Usage();
            }
        }

        var scenario = new FakeChannelScenario
        {
            Fault = fault,
            ReturnSignal = returnSignal,
            ChannelName = channelName,
        };
        if (!scenario.Permissions.Exists(p => p.DungeonId == dungeonId))
        {
            scenario.Permissions.Add((dungeonId, 3));
        }
        scenario.Rooms.Clear();
        for (int r = 0; r < roomCount; r++)
        {
            int[] mobs = new int[monsterCount];
            for (int m = 0; m < monsterCount; m++)
            {
                mobs[m] = 9100 + (r * 100) + m;
            }
            scenario.Rooms.Add(new FakeRoomSpec
            {
                CellX = (byte)r,
                CellY = 0,
                MapId = (ushort)(905 + r),
                MonsterServerIds = mobs,
            });
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        using var stack = new LoopbackOnlineStack(scenario);
        if (!string.IsNullOrEmpty(devRootDir))
        {
            stack.WriteSyntheticDevRoot(devRootDir);
        }

        string readyText =
            "gateway_port=" + stack.GatewayPort + "\n" +
            "dir_port=" + stack.DirectoryPort + "\n" +
            "channel_port=" + stack.ChannelPort + "\n";

        if (!string.IsNullOrEmpty(readyFile))
        {
            string tmp = readyFile + ".tmp";
            File.WriteAllText(tmp, readyText);
            File.Move(tmp, readyFile, overwrite: true);
        }
        Console.WriteLine("DNF_NETFAKE ready fault=" + fault + " rooms=" + roomCount + " monsters=" + monsterCount);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        Console.WriteLine("DNF_NETFAKE stop cmds=" + stack.ReceivedCommands.Count + " conns=" + stack.ChannelAcceptedConnections);
        return 0;
    }

    private static bool TryParseFault(string s, out FakeFaultMode fault)
    {
        switch (s)
        {
            case "none": fault = FakeFaultMode.None; return true;
            case "auth_fail": fault = FakeFaultMode.AuthFail; return true;
            case "select_refused": fault = FakeFaultMode.SelectRefused; return true;
            case "mid_disconnect": fault = FakeFaultMode.MidDisconnect; return true;
            case "settle_incomplete": fault = FakeFaultMode.SettleIncomplete; return true;
            case "malformed_reply": fault = FakeFaultMode.MalformedReply; return true;
            default: fault = FakeFaultMode.None; return false;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("用法: dnf-netfake [--ready-file <路径>] [--write-dev-root <目录>] [--fault none|auth_fail|select_refused|mid_disconnect|settle_incomplete|malformed_reply] [--return-signal area|noti2] [--rooms 1..8] [--monsters 1..16] [--dungeon-id <u16>] [--channel-name <名>]");
        return 64;
    }
}
