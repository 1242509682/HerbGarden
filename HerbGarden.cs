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
    public override Version Version => new(1, 0, 0);
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

        var tile = Main.tile[x, y];

        // 如果不是随机
        if (!Config.Random)
        {
            // 如果草药已经成熟，立即收割
            orig(x, y);

            Task.Run(() => { AutoPlace(x, y, tile); });
        }
        else
        {

            Task.Run(() =>
            {
                if (tile.liquid > 0)
                {
                    int style = tile.frameX / 18;

                    if ((!tile.lava() || style != 5) && (tile.liquidType() != LiquidID.Water || (style != 1 && style != 4)))
                    {
                        WorldGen.KillTile(x, y);
                        NetMessage.SendTileSquare(-1, x, y);
                        WorldGen.SquareTileFrame(x, y);
                    }
                }

                if (tile.type == TileID.ImmatureHerbs)
                {
                    if (WorldGen.genRand.Next(1) == 0)
                    {
                        tile.type = TileID.MatureHerbs;
                        NetMessage.SendTileSquare(-1, x, y);
                        WorldGen.SquareTileFrame(x, y);
                    }
                }
                else if (tile.type == TileID.MatureHerbs)
                {
                    if (WorldGen.genRand.Next(1) == 0)
                    {
                        tile.type = TileID.BloomingHerbs;
                        NetMessage.SendTileSquare(-1, x, y);
                        WorldGen.SquareTileFrame(x, y);
                    }
                }

                AutoPlace(x, y, tile);
            });
        }
    }

    // 自动种植方法
    private static void AutoPlace(int x, int y, ITile tile)
    {
        if ((tile?.active()) != true || tile.type != TileID.BloomingHerbs)
        {
            return;
        }

        var drop = GetItemFromTile(x, y);
        var oldStack = drop.stack < 1 ? 1 : drop.stack;
        var stack = Config.Stack > 0 ? Config.Stack : oldStack;
        int idx = Item.NewItem(null, x * 16 + 8, y * 16 + 8, 0, 0, drop.type, stack);
        NetMessage.SendData((int)PacketTypes.ItemDrop, -1, -1, null, idx);

        WorldGen.KillTile(x, y);
        int style = GetStyleFromItemID(drop.type);
        WorldGen.PlaceTile(x, y, TileID.ImmatureHerbs, true, false, -1, style);
        tile.type = TileID.ImmatureHerbs;
        NetMessage.SendTileSquare(-1, x, y);
        WorldGen.SquareTileFrame(x, y);
    }

    // 获取破坏图格的物品属性
    public static WorldItem GetItemFromTile(int x, int y)
    {
        var noPrefix = false;
        WorldGen.KillTile_GetItemDrops(x, y, Main.tile[x, y], out int type, out int stack, out _, out _, out noPrefix);
        WorldItem item = new();
        item.SetDefaults(type);
        item.stack = stack;
        return item;
    }

    // 根据成熟草药的 物品ID 映射到图格样式
    private static int GetStyleFromItemID(int type)
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
            default: return 0;
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

        int time = Config.CoolDown < 1 ? 60 : Config.CoolDown;
        if (!Wiring.CheckMech(x, y, time) || Chest.IsLocked(x, y))
            return;

        int c = Chest.FindChest(x, y);
        if (c == -1 || Chest.UsingChest(c) != -1) return;

        int range = Config.Range * 16;
        Vector2 size = new Vector2(range * 2, range * 2);
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
    }
    #endregion

    // 草药列表（暂时没用）
    private static readonly int[] Herbs = Enumerable.Empty<int>()
        .Concat(Enumerable.Range(ItemID.DaybloomSeeds, 12))
        .Concat(Enumerable.Range(ItemID.ShiverthornSeeds, 2))
        .ToArray();

}