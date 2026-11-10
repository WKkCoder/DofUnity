using Dnf.Data;
using Dnf.Game;
using Dnf.Game.Tests;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 宠物城镇跟随一线的合成数据与测试台。编号全是合成的:宠物 901(有外观:站立 + 行走,奔跑写成空字符串)、902(选择成长型,没写任何外观)、
/// 903(普通型,只有站立动画,没写停靠距离与速度);宠物物品 950、952、951 依次对应它们;饲料(可叠加物品类型 [feed])980、981,不是饲料的可叠加物品 982。
/// .cre 里的数(离地高度、停靠距离、五个速度)都是测试自己编的,与原版数据无关;经验表 10、30、60、100。
/// </summary>
internal static class PetTownKit
{
    public const int PetFull = 901;
    public const int PetGrow = 902;
    public const int PetPlain = 903;

    public const int ItemFull = 950;
    public const int ItemPlain = 951;
    public const int ItemGrow = 952;

    public const int FeedItem = 980;
    public const int FeedItemB = 981;
    public const int NotFeedItem = 982;

    /// <summary>901 的 .cre 里写的离地高度、停靠距离、五个速度(合成数)。</summary>
    public const int FullHeight = 37;
    public const int FullSpan = -41;
    public static readonly int[] FullSpeed = { 330, 270, 130, 105, 60 };

    public const string FullName = "name_901";

    /// <summary>饲料(可叠加物品类型 [feed])的类型号:直接问标签表,不经过被测的窗口常量。</summary>
    public static readonly int FeedType = StackableTypes.FromTag("[feed]");

    public static Fixture Scripts()
    {
        return new Fixture()
            .List(PetCatalog.ListPath, (901, "p901/p901.cre"), (902, "p902/p902.cre"), (903, "p903/p903.cre"))
            .Tokens("creature/p901/p901.cre", w => w.Tag("[name]").Str(FullName).Tag("[max level]").Int(5)
                .Tag("[floating height]").Int(FullHeight).Tag("[character span]").Int(FullSpan)
                .Tag("[move speed]").Ints(FullSpeed)
                .Tag("[basic motion]").Str("Motion/Stay.ani").Tag("[walk motion]").Str("Motion/Walk.ani").Tag("[run motion]").Str(""))
            .Tokens("creature/p902/p902.cre", w => w.Tag("[max level]").Int(5)
                .Tag("[selective grow creature]").Tag("[using exp item]").Int(1).Tag("[grow Atype item index]").Int(1).Tag("[/selective grow creature]"))
            .Tokens("creature/p903/p903.cre", w => w.Tag("[max level]").Int(5).Tag("[basic motion]").Str("Motion/Stay.ani"))
            .Tokens("equipment/test/pet950.equ", w => w.Tag("[creature species]").Int(901))
            .Tokens("equipment/test/pet951.equ", w => w.Tag("[creature species]").Int(903))
            .Tokens("equipment/test/pet952.equ", w => w.Tag("[creature species]").Int(902))
            .Tokens(PetCatalog.ExpTablePath, w => w.Ints(10, 30, 60, 100));
    }

    public static readonly Dictionary<int, ItemInfo> Items = MakeItems();

    private static Dictionary<int, ItemInfo> MakeItems()
    {
        ItemInfo Pet(int code) => new ItemInfo(code, "equipment/test/pet" + code + ".equ", ItemKind.Equipment) { Part = EquipmentParts.Creature, MinimumLevel = 1 };
        ItemInfo Stack(int code, int type) => new ItemInfo(code, "stackable/test/s" + code + ".stk", ItemKind.Stackable) { StackableType = type };
        return new Dictionary<int, ItemInfo>
        {
            [ItemFull] = Pet(ItemFull),
            [ItemPlain] = Pet(ItemPlain),
            [ItemGrow] = Pet(ItemGrow),
            [FeedItem] = Stack(FeedItem, FeedType),
            [FeedItemB] = Stack(FeedItemB, FeedType),
            [NotFeedItem] = Stack(NotFeedItem, StackableTypes.Waste),
        };
    }

    public static Inventory Bag(params (int Code, int Count)[] stacks)
    {
        var bag = new Inventory();
        foreach ((int code, int count) in stacks)
        {
            Assert.Equal(count, bag.Add(Items[code], count));
        }
        return bag;
    }

    /// <summary>简单的跟随规则,方便手算:每步 100 毫秒时追赶率乘时间正好是 0.5。</summary>
    public static readonly PetFollowRules SimpleRules = new PetFollowRules(chaseRate: 5.0, arriveRadius: 3.0, teleportDistanceX: 400.0, teleportDistanceY: 200.0,
        runDistance: 120.0, homeDepth: 0.0, defaultSpan: -64.0, defaultSpeedX: 400.0, defaultSpeedY: 100.0);

    /// <summary>一整套:数据、会话、手动钟、外观目录、记录调用的精灵来源、城镇宠物场。</summary>
    public sealed class Rig
    {
        public Rig(PetFollowRules rules = null, bool withSprites = true, bool withLooks = true, int startSeconds = 0)
        {
            Data = new GameData(Scripts().Build());
            Session = new PetSession(new PetCatalog(Data));
            Clock = new ManualPetClock(startSeconds);
            Looks = withLooks ? new PetLookCatalog(Data) : null;
            Sprites = withSprites ? new RecordingSprites() : null;
            Field = new TownPetField(Session, Clock, Looks, Sprites, rules ?? SimpleRules);
            Window = new PetWindow(Session, Clock);
        }

        public GameData Data { get; }

        public PetSession Session { get; }

        public ManualPetClock Clock { get; }

        public PetLookCatalog Looks { get; }

        public RecordingSprites Sprites { get; }

        public TownPetField Field { get; }

        public PetWindow Window { get; }

        /// <summary>领养一只(物品编号 <paramref name="itemCode"/>),此刻的钟。</summary>
        public PetInstance Adopt(int itemCode)
        {
            PetInstance pet = Session.Adopt(Items[itemCode], Clock.NowSeconds);
            Assert.NotNull(pet);
            return pet;
        }

        /// <summary>领养并出战(不在战斗里)。</summary>
        public PetInstance AdoptAndEquip(int itemCode, bool fighting = false)
        {
            PetInstance pet = Adopt(itemCode);
            Assert.Equal(PetEquipResult.Ok, Session.Equip(pet.Uid, Clock.NowSeconds, fighting));
            return pet;
        }

        /// <summary>
        /// 用一份合成存档把名册直接还原成指定的样子(能指定每只的整数饱食):<paramref name="equippedUid"/> 为 0 表示谁也不出战;
        /// <paramref name="fighting"/> 为 true 时出战的宠物进"消耗中"(饿死的宠物因此不会在还原时复活)。
        /// </summary>
        public void Restore(int equippedUid, bool fighting, params (int Uid, int ItemCode, int Exp, int Stomach)[] pets)
        {
            var save = new HeroSave();
            foreach ((int uid, int itemCode, int exp, int stomach) in pets)
            {
                save.Pets.Add(new SavedPet(uid, itemCode, exp, stomach));
            }
            if (equippedUid > 0)
            {
                save.PetSlots.Add(new SavedPetSlot(PetEquipment.PetPart, equippedUid));
            }
            PetRestoreReport report = Session.Restore(save, code => Items.TryGetValue(code, out ItemInfo item) ? item : null, Clock.NowSeconds, fighting);
            Assert.Equal(pets.Length, report.Pets);
            Assert.Equal(equippedUid > 0, report.PetEquipped);
        }
    }
}

/// <summary>记下每次被问到什么的假精灵来源:给一块 1×1 的像素,屏幕位置按投影的加法模型算(帧偏移都是 0),排序键是世界 y。</summary>
internal sealed class RecordingSprites : IPetSpriteSource
{
    public sealed record Call(PetLook Look, PetMotion Motion, long AnimMs, int X, int Y, int Z, bool FacingLeft, TownProjection Projection, int ObjectId);

    private static readonly SpriteFrame Pixel = new SpriteFrame(1, 1, 0, 0, 1, 1, new byte[4]);

    public List<Call> Calls { get; } = new List<Call>();

    /// <summary>为 true 时只记录、不给精灵(模拟这一帧没有图)。</summary>
    public bool ReturnNothing { get; set; }

    public SceneSprite Build(PetLook look, PetMotion motion, long animMs, int worldX, int worldY, int worldZ, bool facingLeft, TownProjection projection, int objectId)
    {
        Calls.Add(new Call(look, motion, animMs, worldX, worldY, worldZ, facingLeft, projection, objectId));
        if (ReturnNothing)
        {
            return null;
        }
        return new SceneSprite(Pixel, worldX - projection.CameraX, worldY - worldZ + projection.GroundY, worldY, objectId, look?.AniOf(motion), 0, facingLeft);
    }
}
