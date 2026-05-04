using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using static Plugin.Utils;

namespace Plugin;

[ApiVersion(2, 1)]
public class HerbGarden : TerrariaPlugin
{
    #region 插件信息
    public static string PluginName => "草药园";
    public override string Name => PluginName;
    public override string Author => "羽学";
    public override Version Version => new(1, 0, 5);
    public override string Description => "电路生长+延迟收割，种子自动合成草药袋。";
    #endregion

    #region 文件路径
    public static readonly string Paths = Path.Combine(TShock.SavePath, $"{PluginName}.json");
    #endregion

    #region 注册与释放
    public HerbGarden(Main game) : base(game) { }
    public override void Initialize()
    {
        LoadConfig();
        GeneralHooks.ReloadEvent += ReloadConfig;
        On.Terraria.Wiring.Hopper += OnHopper;
        On.Terraria.Wiring.HitWireSingle += OnHitWireSingle;
        ServerApi.Hooks.GameUpdate.Register(this, OnGameUpdate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GeneralHooks.ReloadEvent -= ReloadConfig;
            On.Terraria.Wiring.Hopper -= OnHopper;
            On.Terraria.Wiring.HitWireSingle -= OnHitWireSingle;
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGameUpdate);
        }
        base.Dispose(disposing);
    }
    #endregion

    #region 配置
    internal static Configuration Config = new();
    private static void ReloadConfig(ReloadEventArgs args)
    {
        LoadConfig();
        args.Player.SendMessage($"[{PluginName}]重新加载配置完毕。", color);
    }
    private static void LoadConfig()
    {
        Config = Configuration.Read();
        Config.Write();
    }
    #endregion

    #region 延迟收割队列
    // 收割任务：记录需要收割的区域参数和执行帧
    private struct HarvestJob
    {
        public int cx, cy, ci;   // 中心坐标、箱子索引
        public long frame;        // 计划执行帧
    }
    private static List<HarvestJob> Queue = new(); // 任务队列
    #endregion

    #region 帧计数器与电路冷却
    public static long Timer = 0;
    private void OnGameUpdate(EventArgs args)
    {
        Timer++;

        // 处理延迟收割队列（逐帧消费）
        for (int i = Queue.Count - 1; i >= 0; i--)
        {
            var job = Queue[i];
            if (Timer >= job.frame) // 到达执行时间
            {
                // 收割、播种、合成草药袋
                DoHarvest(job.cx, job.cy, Config.HopperRange, job.ci);
                Queue.RemoveAt(i);
            }
        }
    }
    #endregion

    #region 电路触发（只生长，不收割）
    private static Dictionary<Point, long> LastHit = new(); // 电路冷却
    private void OnHitWireSingle(On.Terraria.Wiring.orig_HitWireSingle orig, int x, int y)
    {
        orig(x, y);
        if (!Config.Enabled) return;

        // 获取箱子坐标
        var tile = Main.tile[x, y];
        int cx = x, cy = y;
        if (tile.frameX % 36 != 0) cx--;
        if (tile.frameY % 36 != 0) cy--;

        int ci = Chest.FindChest(cx, cy);
        if (ci == -1) return;
        if (Chest.IsLocked(cx, cy)) return;

        // 随机冷却（帧）
        int min = Config.MinCd;
        int max = Config.MaxCd;
        if (min < 1) min = 60;
        if (max < min) max = min;
        int cd = (min == max) ? min : Main.rand.Next(min, max + 1);

        Point key = new Point(cx, cy);
        if (LastHit.TryGetValue(key, out long last) && Timer - last < cd) return;
        LastHit[key] = Timer;

        // 只执行生长，不收割
        GrowRange(cx, cy, Config.HopperRange);

        // 将收割任务加入队列，延迟 60 帧后执行
        int delay = Config.DelayFrames < 1 ? 60 : Config.DelayFrames; // 默认延迟1秒
        Queue.Add(new HarvestJob { cx = cx, cy = cy, ci = ci, frame = Timer + delay });
    }
    #endregion

    #region 核心：生长（逐格概率成长）
    /// <summary>
    /// 对箱子周围矩形范围内的草药进行强制生长（幼苗→成熟→开花）
    /// </summary>
    private void GrowRange(int cx, int cy, int r)
    {
        int x1 = Math.Max(cx - r, 0);
        int x2 = Math.Min(cx + r, Main.maxTilesX - 1);
        int y1 = Math.Max(cy - r, 0);
        int y2 = Math.Min(cy + r, Main.maxTilesY - 1);
        for (int i = x1; i <= x2; i++)
            for (int j = y1; j <= y2; j++)
            {
                ITile t = Main.tile[i, j];
                if (t == null || !t.active()) continue;
                if (t.type == TileID.ImmatureHerbs && WorldGen.genRand.Next(Config.RandomRate) == 0)
                {
                    t.type = TileID.MatureHerbs;
                    NetMessage.SendTileSquare(-1, i, j);
                    WorldGen.SquareTileFrame(i, j);
                }
                else if (t.type == TileID.MatureHerbs && WorldGen.genRand.Next(Config.RandomRate) == 0)
                {
                    t.type = TileID.BloomingHerbs;
                    NetMessage.SendTileSquare(-1, i, j);
                    WorldGen.SquareTileFrame(i, j);
                }
            }
    }
    #endregion

    #region 延迟执行：收割 + 播种 + 合成草药袋
    /// <summary>
    /// 执行收割、播种和合成（由定时器触发）
    /// </summary>
    private void DoHarvest(int cx, int cy, int r, int ci)
    {
        var herbs = new List<Point>();
        GetBloom(cx, cy, r, herbs);
        if (herbs.Count == 0) return;

        foreach (Point p in herbs)
        {
            int rx = p.X, ry = p.Y;
            int herb = HerbID(rx, ry);
            if (herb == 0) continue;

            // 原版收割（掉落草药+种子）
            WorldGen.KillTile(rx, ry);
            NetMessage.SendTileSquare(-1, rx, ry);
            WorldGen.SquareTileFrame(rx, ry);

            // 强制播种（不消耗种子）
            int sty = GetStyle(herb);
            WorldGen.PlaceTile(rx, ry, TileID.ImmatureHerbs, true, false, -1, sty);
            NetMessage.SendTileSquare(-1, rx, ry);
            WorldGen.SquareTileFrame(rx, ry);
        }

        // 尝试合成草药袋（需要七种种子各至少1个）
        TryMakeBag(ci);
    }

    /// <summary>
    /// 收集范围内所有开花草药坐标
    /// </summary>
    private void GetBloom(int cx, int cy, int r, List<Point> outList)
    {
        outList.Clear();
        int x1 = Math.Max(cx - r, 0);
        int x2 = Math.Min(cx + r, Main.maxTilesX - 1);
        int y1 = Math.Max(cy - r, 0);
        int y2 = Math.Min(cy + r, Main.maxTilesY - 1);
        for (int i = x1; i <= x2; i++)
            for (int j = y1; j <= y2; j++)
            {
                ITile t = Main.tile[i, j];
                if (t != null && t.active() && t.type == TileID.BloomingHerbs)
                    outList.Add(new Point(i, j));
            }
    }

    /// <summary>
    /// 从箱子中消耗七种种子各1个，合成1个草药袋（支持堆叠）
    /// </summary>
    private void TryMakeBag(int cidx)
    {
        Chest chest = Main.chest[cidx];
        if (chest == null) return;

        // 检查每种种子是否至少1个
        int[] cnt = new int[AllSeeds.Length];
        bool full = true;
        for (int i = 0; i < AllSeeds.Length; i++)
        {
            int seed = AllSeeds[i];
            int have = 0;
            for (int j = 0; j < chest.item.Length; j++)
            {
                if (chest.item[j].type == seed && chest.item[j].stack > 0)
                    have += chest.item[j].stack;
            }
            cnt[i] = have;
            if (have < 1) full = false;
        }
        if (!full) return;

        // 扣除每种种子一个
        for (int i = 0; i < AllSeeds.Length; i++)
        {
            int seed = AllSeeds[i];
            for (int j = 0; j < chest.item.Length; j++)
            {
                if (chest.item[j].type == seed && chest.item[j].stack > 0)
                {
                    chest.item[j].stack--;
                    if (chest.item[j].stack <= 0) chest.item[j].TurnToAir();
                    NetMessage.SendData((int)PacketTypes.ChestItem, -1, -1, null, cidx, j);
                    break;
                }
            }
        }

        // 尝试堆叠到已有草药袋
        bool done = false;
        for (int i = 0; i < chest.item.Length; i++)
        {
            if (chest.item[i].type == ItemID.HerbBag && chest.item[i].stack < chest.item[i].maxStack)
            {
                chest.item[i].stack++;
                NetMessage.SendData((int)PacketTypes.ChestItem, -1, -1, null, cidx, i);
                done = true;
                break;
            }
        }

        // 没有可堆叠的，放空槽位
        if (!done)
        {
            for (int i = 0; i < chest.item.Length; i++)
            {
                if (chest.item[i].IsAir)
                {
                    Item bag = ContentSamples.ItemsByType[ItemID.HerbBag].Clone();
                    bag.stack = 1;
                    chest.item[i] = bag;
                    NetMessage.SendData((int)PacketTypes.ChestItem, -1, -1, null, cidx, i);
                    done = true;
                    break;
                }
            }
        }

        // 箱子满则掉落地面
        if (!done)
        {
            int idx = Item.NewItem(null, chest.x * 16 + 16, chest.y * 16 + 16, 0, 0, ItemID.HerbBag, 1);
            NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, idx);
        }
    }
    #endregion

    #region 漏斗扩大吸入范围（仅草药）
    private void OnHopper(On.Terraria.Wiring.orig_Hopper orig, int sx, int sy)
    {
        if (!Config.Enabled)
        {
            orig(sx, sy);
            return;
        }

        var tile = Main.tile[sx, sy];
        int x = sx, y = sy;
        if (tile.frameX % 36 != 0) x--;
        if (tile.frameY % 36 != 0) y--;

        int chestIdx = Chest.FindChest(x, y);
        if (chestIdx == -1) return;

        // 原版范围 3 格，自定义范围（配置）
        int vanR = 3;
        int vanPr = vanR * 16;
        int cusR = Config.HopperRange;
        int cusPr = cusR * 16;

        Vector2 cen = new Vector2(x * 16 + 16, y * 16 + 16);
        Rectangle vanArea = Terraria.Utils.CenteredRectangle(cen, new Vector2(vanPr * 2, vanPr * 2));
        Rectangle cusArea = Terraria.Utils.CenteredRectangle(cen, new Vector2(cusPr * 2, cusPr * 2));

        for (int i = 0; i < Main.item.Length; i++)
        {
            WorldItem it = Main.item[i];
            if (!it.active) continue;
            if (ItemID.Sets.ItemsThatShouldNotBeInInventory[it.type]) continue;

            bool isHerb = Herbs.Contains(it.type);
            bool inRange = isHerb ? it.Hitbox.Intersects(cusArea) : it.Hitbox.Intersects(vanArea);
            if (!inRange) continue;

            Chest.VisualizeChestTransfer(it.Center, cen, it.type, Chest.ItemTransferVisualizationSettings.Hopper);
            if (Wiring.TryToPutItemInChest(i, chestIdx))
                NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, i);
        }
    }
    #endregion

    #region 辅助映射方法
    private static int HerbID(int x, int y)
    {
        ITile t = Main.tile[x, y];
        if (t == null || t.type != TileID.BloomingHerbs) return 0;
        switch (t.frameX / 18)
        {
            case 0: return ItemID.Daybloom;
            case 1: return ItemID.Moonglow;
            case 2: return ItemID.Blinkroot;
            case 3: return ItemID.Deathweed;
            case 4: return ItemID.Waterleaf;
            case 5: return ItemID.Fireblossom;
            case 6: return ItemID.Shiverthorn;
            default: return 0;
        }
    }

    private static int GetStyle(int herb)
    {
        switch (herb)
        {
            case ItemID.Daybloom: return 0;
            case ItemID.Moonglow: return 1;
            case ItemID.Blinkroot: return 2;
            case ItemID.Deathweed: return 3;
            case ItemID.Waterleaf: return 4;
            case ItemID.Fireblossom: return 5;
            case ItemID.Shiverthorn: return 6;
            default: return WorldGen.genRand.Next(7);
        }
    }

    // 全部七种种子（用于合成草药袋）
    private static readonly int[] AllSeeds =
    [
        ItemID.DaybloomSeeds,
        ItemID.MoonglowSeeds,
        ItemID.BlinkrootSeeds,
        ItemID.DeathweedSeeds,
        ItemID.WaterleafSeeds,
        ItemID.FireblossomSeeds,
        ItemID.ShiverthornSeeds
    ];
    // 草药集合（用于漏斗范围判断）
    private static readonly int[] Herbs =
    [
        ItemID.Daybloom, ItemID.Moonglow, ItemID.Blinkroot, ItemID.Deathweed,
        ItemID.Waterleaf, ItemID.Fireblossom, ItemID.Shiverthorn,
        ItemID.DaybloomSeeds, ItemID.MoonglowSeeds, ItemID.BlinkrootSeeds,
        ItemID.DeathweedSeeds, ItemID.WaterleafSeeds, ItemID.FireblossomSeeds,
        ItemID.ShiverthornSeeds
    ];
    #endregion
}