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
    public static string PluginName => "草药园"; // 插件名称
    public override string Name => PluginName;
    public override string Author => "羽学";
    public override Version Version => new(1, 0, 2);
    public override string Description => "草药无视原版条件生长，实现自动掉落与种植，扩大电线箱子吸入物品范围等功能";
    #endregion

    #region 文件路径
    public static readonly string Paths = Path.Combine(TShock.SavePath, $"{PluginName}.json"); // 配置文件路径
    #endregion

    #region 注册与释放
    public HerbGarden(Main game) : base(game) { }
    public override void Initialize()
    {
        LoadConfig(); // 加载配置文件
        GeneralHooks.ReloadEvent += ReloadConfig;
        On.Terraria.WorldGen.GrowAlch += GrowAlch;
        On.Terraria.Wiring.Hopper += Hopper;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GeneralHooks.ReloadEvent -= ReloadConfig;
            On.Terraria.WorldGen.GrowAlch -= GrowAlch;
            On.Terraria.Wiring.Hopper -= Hopper;
        }
        base.Dispose(disposing);
    }
    #endregion

    #region 配置重载读取与写入方法
    internal static Configuration Config = new(); // 配置文件实例
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

    #region 草药无条件生长方法
    public static void GrowAlch(On.Terraria.WorldGen.orig_GrowAlch orig, int x, int y)
    {
        if (!Config.Enabled)
        {
            orig(x, y); // 执行原版生长逻辑
            return;
        }

        // 如果不是随机
        if (!Config.Random)
        {
            orig(x, y);
        }
        else
        {
            if (Main.tile[x, y].liquid > 0)
            {
                int style = Main.tile[x, y].frameX / 18;

                if ((!Main.tile[x, y].lava() || style != 5) && (Main.tile[x, y].liquidType() != LiquidID.Water || (style != 1 && style != 4)))
                {
                    WorldGen.KillTile(x, y);
                    NetMessage.SendTileSquare(-1, x, y);
                    WorldGen.SquareTileFrame(x, y);
                }
            }

            if (Main.tile[x, y].type == TileID.ImmatureHerbs)
            {
                Main.tile[x, y].type = TileID.MatureHerbs;
                NetMessage.SendTileSquare(-1, x, y);
                WorldGen.SquareTileFrame(x, y);
            }
            else if (Main.tile[x, y].type == TileID.MatureHerbs)
            {
                Main.tile[x, y].type = TileID.BloomingHerbs;
                NetMessage.SendTileSquare(-1, x, y);
                WorldGen.SquareTileFrame(x, y);
            }
        }
    }
    #endregion

    #region 吸入箱子方法，改了范围
    private void Hopper(On.Terraria.Wiring.orig_Hopper orig, int sourceX, int sourceY)
    {
        if (!Config.Enabled)
        {
            orig(sourceX, sourceY);
            return;
        }

        var tile = Main.tile[sourceX, sourceY];
        int x = sourceX;
        int y = sourceY;
        if (tile.frameX % 36 != 0) x--;
        if (tile.frameY % 36 != 0) y--;

        int time = Config.HopperCooldown < 1 ? 60 : Config.HopperCooldown;
        if (!Wiring.CheckMech(x, y, time) || Chest.IsLocked(x, y))
            return;

        int c = Chest.FindChest(x, y);
        if (c == -1 || Chest.UsingChest(c) != -1) return;

        int tileRange = Config.HopperRange; // 图格半径
        int pixelRange = tileRange * 16; // 像素半径
        Vector2 size = new Vector2(pixelRange * 2, pixelRange * 2);
        Vector2 center = new Vector2(x * 16 + 16, y * 16 + 16);
        Rectangle Rect = Terraria.Utils.CenteredRectangle(center, size);

        for (int i = 0; i < Main.item.Length; i++)
        {
            var item = Main.item[i];

            if (item.active && item.Hitbox.Intersects(Rect) &&
                !ItemID.Sets.ItemsThatShouldNotBeInInventory[item.type])
            {
                // 先播放动画
                Chest.VisualizeChestTransfer(item.Center, Rect.Center.ToVector2(), item.type, Chest.ItemTransferVisualizationSettings.Hopper);

                // 再尝试放入箱子
                if (Wiring.TryToPutItemInChest(i, c))
                {
                    Terraria.UI.ItemSorting.SortInventory(Main.chest[c], false, false);
                    NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, i);
                }
            }
        }

        // 自动播种
        int minX = Math.Max(x - tileRange, 0);
        int maxX = Math.Min(x + tileRange, Main.maxTilesX - 1);
        int minY = Math.Max(y - tileRange, 0);
        int maxY = Math.Min(y + tileRange, Main.maxTilesY - 1);
        for (int tx = minX; tx <= maxX; tx++)
        {
            for (int ty = minY; ty <= maxY; ty++)
            {
                var checkTile = Main.tile[tx, ty];
                if (checkTile?.active() == true && checkTile.type == TileID.BloomingHerbs)
                {
                    // 传递箱子坐标
                    AutoPlace(tx, ty, x, y, c);
                }
            }
        }
    }
    #endregion

    #region 自动播种方法
    private static void AutoPlace(int x, int y, int cX, int cY, int chestIndex)
    {
        var tile = Main.tile[x, y];
        if (tile?.active() != true) return;

        // 1. 先获取掉落物类型（基于当前成熟图格）
        var drop = GetItem(x, y);
        var herbType = drop.type;
        if (herbType == 0) return;

        // 2. 获取对应的种子ID
        int seedType = GetSeed(herbType);

        // 3. 消耗种子
        if (seedType != 0)
        {
            var chest = Main.chest[chestIndex];
            int Slot = -1;
            for (int i = 0; i < chest.item.Length; i++)
            {
                if (chest.item[i].type == seedType && chest.item[i].stack > 0)
                {
                    Slot = i;
                    break;
                }
            }

            if (Slot != -1)
            {
                // 扣除一个种子
                chest.item[Slot].stack--;
                if (chest.item[Slot].stack <= 0)
                    chest.item[Slot].TurnToAir();
                NetMessage.SendData((int)PacketTypes.ChestItem, -1, -1, null, chestIndex, Slot);
            }
        }

        // 4. 生成草药掉落物
        int stack = Config.Stack > 0 ? Config.Stack : (drop.stack < 1 ? 1 : drop.stack);
        int idx = Item.NewItem(null, x * 16 + 8, y * 16 + 8, 0, 0, herbType, stack);
        NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, idx);

        // 5. 清除原图格并重新种植幼苗
        WorldGen.KillTile(x, y);
        int style = GetStyle(herbType);
        WorldGen.PlaceTile(x, y, TileID.ImmatureHerbs, true, false, -1, style);

        // 6. 刷新图格网络
        NetMessage.SendTileSquare(-1, x, y);
        WorldGen.SquareTileFrame(x, y);

        // 7. 报告自动播种
        if (Config.Broadcast)
        {
            // 计算距离
            int distance = Math.Max(Math.Abs(x - cX), Math.Abs(y - cY));
            TShock.Utils.Broadcast($"[自动播种] {ItemIcon(drop.type)} 距离箱子:{distance} 格 ({x},{y}) ", color2);
        }
    }

    // 获取破坏图格的物品属性
    public static WorldItem GetItem(int x, int y)
    {
        var noPrefix = false;
        WorldGen.KillTile_GetItemDrops(x, y, Main.tile[x, y], out int type, out int stack, out _, out _, out noPrefix);
        WorldItem item = new();
        item.SetDefaults(type);
        item.stack = stack;
        return item;
    }

    // 根据成熟草药的 物品ID 映射到图格样式
    private static int GetStyle(int type)
    {
        switch (type)
        {
            case ItemID.Daybloom: return 0; // 太阳花
            case ItemID.Moonglow: return 1; // 月光草
            case ItemID.Blinkroot: return 2; // 闪耀根
            case ItemID.Deathweed: return 3; // 死亡草
            case ItemID.Waterleaf: return 4; //  幌菊
            case ItemID.Fireblossom: return 5; // 火焰花
            case ItemID.Shiverthorn: return 6; // 寒颤棘
            default: return WorldGen.genRand.Next(7); // 随机 0-6
        }
    }

    // 获取种子来自物品ID
    private static int GetSeed(int herbType)
    {
        switch (herbType)
        {
            case ItemID.Daybloom: return ItemID.DaybloomSeeds;
            case ItemID.Moonglow: return ItemID.MoonglowSeeds;
            case ItemID.Blinkroot: return ItemID.BlinkrootSeeds;
            case ItemID.Deathweed: return ItemID.DeathweedSeeds;
            case ItemID.Waterleaf: return ItemID.WaterleafSeeds;
            case ItemID.Fireblossom: return ItemID.FireblossomSeeds;
            case ItemID.Shiverthorn: return ItemID.ShiverthornSeeds;
            default: return 0;
        }
    }
    #endregion
}