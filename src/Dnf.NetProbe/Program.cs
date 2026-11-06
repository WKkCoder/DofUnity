using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Dnf.Net.Frame;
using Dnf.Net.Gateway;
using Dnf.Net.Session;

namespace Dnf.NetProbe;

/// <summary>
/// P7 联网探针(命令行,输出一律脱敏:只报状态、长度、计数与失败码,不报地址、账号、令牌与载荷内容)。
/// 机密只在进程内:Config.toml 里的网关地址与密钥、local.secrets.env 里的账号口令与鼠标口令密文,由本程序自己读。
///
/// 用法:
///   dnf-netprobe offline &lt;首包文件&gt;            把保存下来的频道首包喂给会话层,验证解码与频道信息解析(不联网)
///   dnf-netprobe live [--register] [--channel-port N] [--data-version N] [--seconds N]
///        网关健康检查 → 登录(或先注册测试号) → 取游戏服地址 → 连频道 → 收首包 → 发 LOGIN → 收集 N 秒回包
/// 在仓库根目录(或带 local.env 的工作树根目录)下运行。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "offline")
        {
            return await Offline(args);
        }
        if (args.Length >= 1 && args[0] == "live")
        {
            return await Live(args);
        }
        if (args.Length >= 1 && args[0] == "orchestrated")
        {
            return await Orchestrated(args);
        }
        Console.WriteLine("用法: dnf-netprobe offline <首包文件> | live [--register] [--channel-port N] [--data-version N] [--seconds N] | orchestrated [仓库根]");
        return 64;
    }

    // 走产品编排 OnlineTownEntry 的整条链(与 Unity 宿主同路),逐阶段与结局只报脱敏信息。
    // --stay N:进城后继续挂 N 秒(心跳保活泵独占接收),结束时报回过几次心跳与连接是否还活。
    private static async Task<int> Orchestrated(string[] args)
    {
        string repoRoot = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : Directory.GetCurrentDirectory();
        if (!LocalDevEntrySettings.TryLoad(repoRoot, out OnlineEntrySettings settings, out string problem))
        {
            Console.WriteLine("装配失败: " + problem);
            return 2;
        }
        var entry = new OnlineTownEntry(settings);
        OnlineEntryResult result = await entry.RunAsync(stage => Console.WriteLine("阶段: " + stage));
        using (result)
        {
            Console.WriteLine($"结局: 阶段={result.Stage} 失败={result.Failure} 登录失败码={(result.LoginFailureCode.HasValue ? result.LoginFailureCode.Value.ToString() : "-")} 槽={result.Slot} 帧={result.FramesSeen} 第二层不符={result.SecondLayerMismatches}");
            if (!result.Entered)
            {
                return 3;
            }
            if (TryGetOption(args, "--stay", out string stayText) && int.TryParse(stayText, out int staySeconds) && staySeconds > 0)
            {
                Console.WriteLine($"长驻 {staySeconds} 秒(心跳保活中)…");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Task<int> pump = OnlineHeartbeat.RunAsync(result.Session, CancellationToken.None);
                Task winner = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(staySeconds)));
                bool peerClosed = result.Session.PeerClosed;
                int? answered = null;
                if (winner == pump && pump.Status == TaskStatus.RanToCompletion)
                {
                    answered = pump.Result;
                }
                else
                {
                    result.Dispose(); // 关流让泵收尾并交回计数
                    Task done = await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(5)));
                    if (done == pump && pump.Status == TaskStatus.RanToCompletion)
                    {
                        answered = pump.Result;
                    }
                }
                Console.WriteLine($"长驻结束: 已过 {watch.Elapsed.TotalSeconds:F0} 秒, 对端关连接={peerClosed}, 回心跳={(answered.HasValue ? answered.Value.ToString() : "未知")}");
                return peerClosed ? 4 : 0;
            }
            return 0;
        }
    }

    private static bool TryGetOption(string[] args, string name, out string value)
    {
        value = "";
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                value = args[i + 1];
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ 离线

    private static async Task<int> Offline(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("offline 需要首包文件路径");
            return 64;
        }
        byte[] bytes = await File.ReadAllBytesAsync(args[1]);
        using var stream = new MemoryStream(bytes);
        var session = new GameClientSession(stream);
        bool ok = await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("首包字节数: " + bytes.Length);
        Console.WriteLine("启动包解码: " + (ok ? "成功" : (session.Rejected ? "被拒绝帧" : "失败")));
        if (ok && session.Channel != null)
        {
            Console.WriteLine("密钥块字节数: " + session.Channel.KeyBlock.Length);
            Console.WriteLine("频道地址个数: " + session.Channel.Addresses.Count);
        }
        foreach (SessionEvent e in session.Events)
        {
            Console.WriteLine("  " + e);
        }
        return ok ? 0 : 2;
    }

    // ------------------------------------------------------------------ 活体

    private sealed class LocalConfig
    {
        public string RepoRoot;
        public string DnfDir;
        public string GatewayUrl;
        public string AesKeyHex;
        public Dictionary<string, string> Secrets;
    }

    private static async Task<int> Live(string[] args)
    {
        bool register = args.Contains("--register");
        int channelPort = IntArg(args, "--channel-port", 30011);
        uint dataVersion = (uint)IntArg(args, "--data-version", 0);
        int seconds = IntArg(args, "--seconds", 20);
        string slotsArg = StrArg(args, "--select-slots", null);
        string dumpPath = StrArg(args, "--dump", null);

        LocalConfig cfg;
        try
        {
            cfg = LoadConfig();
        }
        catch (Exception e)
        {
            Console.WriteLine("本地配置读取失败: " + e.GetType().Name);
            return 65;
        }

        var options = new GatewayOptions(cfg.GatewayUrl, cfg.AesKeyHex, TimeSpan.FromSeconds(15));
        using var transport = new HttpGatewayTransport(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, options.Timeout);
        using var gateway = new GatewayClient(options, transport);

        GatewayResult health = await gateway.HealthAsync();
        Console.WriteLine("网关健康检查: " + health.Outcome + "(HTTP " + health.HttpStatus + ")");
        if (!health.IsSuccess)
        {
            return 3;
        }

        string account;
        string password;
        if (register)
        {
            string user = "t" + RandomNumberGenerator.GetInt32(10000000, 99999999).ToString();
            string pass = "P" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            GatewayResult reg = await gateway.RegisterAsync(user, pass, "10000");
            Console.WriteLine("注册测试号: " + reg.Outcome + "(HTTP " + reg.HttpStatus + ")");
            if (!reg.IsSuccess)
            {
                return 4;
            }
            AppendSecrets(cfg.RepoRoot, user, pass);
            account = user;
            password = pass;
        }
        else if (cfg.Secrets.TryGetValue("DNF_TEST_ACCOUNT", out string testAccount) && cfg.Secrets.TryGetValue("DNF_TEST_PASSWORD", out string testPass))
        {
            account = testAccount;
            password = testPass;
            Console.WriteLine("使用已保存的测试号登录");
        }
        else if (cfg.Secrets.TryGetValue("DNF_ACCOUNT", out string mainAccount) && cfg.Secrets.TryGetValue("DNF_PASSWORD", out string mainPass))
        {
            account = mainAccount;
            password = mainPass;
            Console.WriteLine("使用本地保存的账号登录(未注册测试号时可用 --register)");
        }
        else
        {
            Console.WriteLine("local.secrets.env 里没有可用账号");
            return 65;
        }

        GatewayResult login = await gateway.LoginAsync(account, password);
        Console.WriteLine("网关登录: " + login.Outcome + "(HTTP " + login.HttpStatus + "),令牌长度 " + (login.Token == null ? 0 : login.Token.Length));
        if (!login.IsSuccess || login.Token == null)
        {
            return 5;
        }
        string token = login.Token;

        GatewayResult ip = await gateway.GetGameServerIpAsync();
        Console.WriteLine("游戏服地址: " + ip.Outcome + "(HTTP " + ip.HttpStatus + "),与本地记录一致: "
            + (ip.Text != null && cfg.Secrets.TryGetValue("DNF_SERVER_IP", out string known) && ip.Text == known));
        if (!ip.IsSuccess || ip.Text == null)
        {
            return 6;
        }

        using var tcp = new TcpClient();
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(ip.Text, channelPort, connectTimeout.Token);
        uint localAddress = 0;
        if (tcp.Client.LocalEndPoint is IPEndPoint local)
        {
            byte[] b = local.Address.GetAddressBytes();
            if (b.Length == 4)
            {
                localAddress = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            }
        }
        Console.WriteLine("频道连接已建立(端口 " + channelPort + ")");

        var session = new GameClientSession(tcp.GetStream());
        bool boot = await session.ReceiveBootstrapAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("启动包: " + (boot ? "已解出密钥块" : (session.Rejected ? "收到拒绝帧" : "超时或失败")));
        if (!boot)
        {
            PrintEvents(session);
            return 7;
        }

        byte[] passwordBlock = Encoding.ASCII.GetBytes(Md5Hex(password));
        byte[] mouseCipher = new byte[LoginRequest.MouseCipherBytes];
        if (cfg.Secrets.TryGetValue("DNF_MOUSE_CIPHER_HEX", out string mouseHex) && mouseHex.Length == 16)
        {
            mouseCipher = Convert.FromHexString(mouseHex);
        }
        else
        {
            Console.WriteLine("注意:没有鼠标口令密文,发全零块(服务端大概率回失败码 1)");
        }

        var request = new LoginRequest
        {
            PatchTime = 0,
            Token = Encoding.ASCII.GetBytes(token),
            UserName = Encoding.ASCII.GetBytes(account),
            PasswordBlock = passwordBlock,
            LoginMode = 0,
            MouseCipher = mouseCipher,
            DataVersion = dataVersion,
            ServerGroup = 0,
            LocalAddress = localAddress,
        };
        LoginRequestProblem problem = request.Validate();
        if (problem != LoginRequestProblem.None)
        {
            Console.WriteLine("登录请求本地检查不过: " + problem);
            return 65;
        }
        FrameStatus sent = await session.SendAsync(PacketClass.Cmd, LoginRequest.PacketId, request.Build());
        Console.WriteLine("LOGIN 已发出: " + sent + "(数据版本 " + dataVersion + ")");

        // 先收登录回包:进程内解析账号号(不打印),校验成功后立刻发 GET_USERINFO 取角色列表
        List<DecodedFrame> frames = await session.ReceiveAsync(TimeSpan.FromSeconds(seconds), stopAfterFrames: 8);
        DecodedFrame loginReply = frames.FirstOrDefault(f => f.Header.Class == PacketClass.Cmd && f.Header.PacketId == LoginRequest.PacketId && f.Payload.Length >= 5 && f.Payload[0] == 1);
        if (loginReply != null)
        {
            byte[] payload = loginReply.Payload;
            int nameLen = BitConverter.ToInt32(payload, 1);
            int accOff = 5 + nameLen + 8;
            bool layoutOk = nameLen >= 0 && nameLen <= 0x14 && accOff + 4 <= payload.Length;
            Console.WriteLine("成功包布局: 名字长度字段=" + nameLen + " 账号号偏移=" + accOff + " 在界内=" + layoutOk);
            if (!layoutOk)
            {
                PrintEvents(session);
                return 1;
            }
            int accId = BitConverter.ToInt32(payload, accOff);
            // 角色列表请求(CMD 1/8):u16 账号号(低 16 位) + u8 列表类型 2(自己的角色列表)
            var charReq = new byte[3];
            charReq[0] = (byte)(accId & 0xff);
            charReq[1] = (byte)((accId >> 8) & 0xff);
            charReq[2] = 2;
            FrameStatus listSent = await session.SendAsync(PacketClass.Cmd, 8, charReq);
            Console.WriteLine("GET_USERINFO(类型 2)已发出: " + listSent + ",等待应答 " + seconds + " 秒…");
            List<DecodedFrame> listFrames = await session.ReceiveAsync(TimeSpan.FromSeconds(seconds), stopAfterFrames: 12);
            frames.AddRange(listFrames);
            DecodedFrame listReply = listFrames.FirstOrDefault(f => f.Header.Class == PacketClass.Noti && f.Header.PacketId == 2 && f.Payload.Length >= 2);
            if (listReply == null)
            {
                Console.WriteLine("未等到角色列表应答(NOTI 0/2;可能被封进 561 信封),继续后续步骤。");
            }
            else
            {
                byte[] lp = listReply.Payload;
                Console.WriteLine("角色列表应答 " + lp.Length + " 字节;前三字节(结构量): " +
                    lp[0] + "," + lp[1] + "," + (lp.Length > 2 ? lp[2].ToString() : "-"));
            }

            if (slotsArg != null)
            {
                // 逐槽选角(CMD 1/4 只带 u8 槽号):选错空槽只回错误码无副作用;成功则继续收集进城包流(只记包号与长度)
                foreach (string part in slotsArg.Split(','))
                {
                    if (!int.TryParse(part.Trim(), out int slot) || slot < 0 || slot > 0x23)
                    {
                        Console.WriteLine("槽位参数无效: " + part);
                        continue;
                    }
                    FrameStatus selSent = await session.SendAsync(PacketClass.Cmd, 4, new byte[] { (byte)slot });
                    List<DecodedFrame> selFrames = await session.ReceiveAsync(TimeSpan.FromSeconds(Math.Max(seconds, 10)), stopAfterFrames: 48);
                    frames.AddRange(selFrames);
                    DecodedFrame reply = selFrames.FirstOrDefault(f => f.Header.Class == PacketClass.Cmd && f.Header.PacketId == 4 && f.Payload.Length >= 1);
                    string seq = string.Join(" ", selFrames.Select(f => (f.Header.Class == PacketClass.Cmd ? "C" : "N") + "/" + f.Header.PacketId + "(" + f.Payload.Length + ")"));
                    // 成功回包与失败错误包都可能被封进 561 信封而没有明文 CMD/4:以包流形态兜底判定——
                    // 无明文失败包且本窗出现单个 >=3000 字节的数据大包(选角成功才下发的角色/城镇数据)视为成功
                    bool burst = reply == null && selFrames.Any(f => f.Payload.Length >= 3000);
                    if ((reply != null && reply.Payload[0] == 1) || burst)
                    {
                        Console.WriteLine("选角 槽 " + slot + ": 成功" + (reply != null ? "(明文成功回包 " + reply.Payload.Length + " 字节)" : "(按进城包流形态判定,成功回包应在信封内)") + ";本窗包流: " + seq);
                        List<DecodedFrame> townFrames = await session.ReceiveAsync(TimeSpan.FromSeconds(seconds), stopAfterFrames: 64);
                        frames.AddRange(townFrames);
                        string townSeq = string.Join(" ", townFrames.Select(f => (f.Header.Class == PacketClass.Cmd ? "C" : "N") + "/" + f.Header.PacketId + "(" + f.Payload.Length + ")"));
                        Console.WriteLine("选角后包流 " + townFrames.Count + " 个: " + townSeq);
                        break;
                    }
                    if (reply != null && reply.Payload[0] == 0)
                    {
                        Console.WriteLine("选角 槽 " + slot + ": 失败,码 " + (reply.Payload.Length >= 2 ? reply.Payload[1].ToString() : "?") + ";本窗包流: " + seq);
                        continue;
                    }
                    Console.WriteLine("选角 槽 " + slot + ": 无 CMD/4 回包(静默失败或回包被封);本窗包流: " + seq);
                }
            }
        }
        PrintEvents(session);
        Console.WriteLine("对端关闭: " + session.PeerClosed + ";第二层不符次数: " + session.SecondLayerMismatches);
        if (dumpPath != null)
        {
            // 把本次收到的解密帧逐条([u8 类][u16 包号][u32 载荷长][载荷])写到本地文件,供离线结构盘点。
            // 文件可能含账号内数据,只许落在本地忽略区,内容永不打印、不入库。
            using var fs = new FileStream(dumpPath, FileMode.Create, FileAccess.Write);
            byte[] tmp = new byte[4];
            foreach (DecodedFrame f in frames)
            {
                fs.WriteByte((byte)f.Header.Class);
                BitConverter.TryWriteBytes(tmp, (ushort)f.Header.PacketId);
                fs.Write(tmp, 0, 2);
                BitConverter.TryWriteBytes(tmp, f.Payload.Length);
                fs.Write(tmp, 0, 4);
                fs.Write(f.Payload, 0, f.Payload.Length);
            }
            Console.WriteLine("已把 " + frames.Count + " 个解密帧写到本地文件(仅供结构盘点)");
        }
        int verdict = 8;
        foreach (DecodedFrame f in frames)
        {
            if (f.Header.Class == PacketClass.Cmd && f.Header.PacketId == LoginRequest.PacketId && f.Payload.Length >= 1)
            {
                if (f.Payload[0] == 1)
                {
                    Console.WriteLine("登录回包: 成功(载荷 " + f.Payload.Length + " 字节)");
                    verdict = 0;
                }
                else if (f.Payload[0] == 0 && f.Payload.Length >= 2)
                {
                    Console.WriteLine("登录回包: 失败,码 " + f.Payload[1] + "(0x" + f.Payload[1].ToString("x2") + "),其后 " + (f.Payload.Length - 2) + " 字节");
                    verdict = 9;
                }
            }
        }
        int noti183 = frames.Count(f => f.Header.Class == PacketClass.Noti && f.Header.PacketId == 183);
        if (noti183 > 0)
        {
            Console.WriteLine("受理前通知 0/183 到达 " + noti183 + " 个(说明 LOGIN 的前置检查都过了)");
        }
        DecodedFrame charList = frames.FirstOrDefault(f => f.Header.Class == PacketClass.Noti && f.Header.PacketId == 2 && f.Payload.Length >= 9);
        if (charList != null)
        {
            var reader = new PacketReader(charList.Payload);
            reader.TryGetByte(out byte listType);
            reader.TryGetByte(out byte slotLimit);
            reader.TryGetByte(out _);
            reader.TryGetByte(out _);
            reader.TryGetInt(out _);
            bool countOk = reader.TryGetShort(out short charCount);
            Console.WriteLine("角色列表: 类型 " + listType + ",槽位上限 " + slotLimit + ",角色数 " + (countOk ? charCount.ToString() : "读不出")
                + ",载荷共 " + charList.Payload.Length + " 字节(名字不打印)");
            verdict = verdict == 0 ? 0 : verdict;
        }
        return verdict;
    }

    private static void PrintEvents(GameClientSession session)
    {
        foreach (SessionEvent e in session.Events)
        {
            Console.WriteLine("  " + e);
        }
    }

    // ------------------------------------------------------------------ 本地配置(只在进程内)

    private static LocalConfig LoadConfig()
    {
        string root = FindRepoRoot();
        Dictionary<string, string> localEnv = ParseEnv(Path.Combine(root, "local.env"));
        string dnfDir = localEnv["DNF_DIR"];
        string configPath = Path.Combine(dnfDir, "Config.toml");
        string gatewayUrl = null;
        string aesKeyHex = null;
        foreach (string raw in File.ReadAllLines(configPath))
        {
            string line = raw.Trim();
            if (line.StartsWith("server_url"))
            {
                gatewayUrl = TomlString(line);
            }
            else if (line.StartsWith("aes_key"))
            {
                aesKeyHex = TomlString(line);
            }
        }
        if (gatewayUrl == null || aesKeyHex == null)
        {
            throw new InvalidOperationException("config");
        }
        return new LocalConfig
        {
            RepoRoot = root,
            DnfDir = dnfDir,
            GatewayUrl = gatewayUrl,
            AesKeyHex = aesKeyHex,
            Secrets = ParseEnv(Path.Combine(root, "local.secrets.env")),
        };
    }

    private static string FindRepoRoot()
    {
        string dir = Environment.CurrentDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "local.env")) && File.Exists(Path.Combine(dir, "local.secrets.env")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("root");
    }

    private static string TomlString(string line)
    {
        int eq = line.IndexOf('=');
        string v = line.Substring(eq + 1).Trim();
        if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
        {
            return v.Substring(1, v.Length - 2);
        }
        return v;
    }

    private static Dictionary<string, string> ParseEnv(string path)
    {
        var result = new Dictionary<string, string>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("export "))
            {
                line = line.Substring(7);
            }
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            string value = line.Substring(eq + 1).Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
            {
                value = value.Substring(1, value.Length - 2);
            }
            result[line.Substring(0, eq).Trim()] = value;
        }
        return result;
    }

    private static void AppendSecrets(string repoRoot, string account, string password)
    {
        string path = Path.Combine(repoRoot, "local.secrets.env");
        string[] lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        lines = lines.Where(l => !l.StartsWith("DNF_TEST_ACCOUNT=") && !l.StartsWith("DNF_TEST_PASSWORD="))
            .Concat(new[] { "DNF_TEST_ACCOUNT=" + account, "DNF_TEST_PASSWORD=" + password }).ToArray();
        File.WriteAllLines(path, lines);
    }

    private static string Md5Hex(string text)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out int v))
            {
                return v;
            }
        }
        return fallback;
    }

    private static string StrArg(string[] args, string name, string fallback)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return fallback;
    }
}
