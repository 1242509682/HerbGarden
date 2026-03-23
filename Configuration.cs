using Newtonsoft.Json;
using static Plugin.HerbGarden;

namespace Plugin;

internal class Configuration
{
    #region 配置项成员
    [JsonProperty("插件开关", Order = 0)]
    public bool Enabled { get; set; } = true;
    [JsonProperty("草药随机生长", Order = 1)]
    public bool Random { get; set; } = true;
    [JsonProperty("草药收割毫秒", Order = 2)]
    public int HarvestCooldown { get; set; } = 1000;
    [JsonProperty("队列处理帧率", Order = 3)]
    public int QueueInterval { get; set; } = 60;
    [JsonProperty("队列处理数量", Order = 4)]
    public int QueueConut { get; set; } = 20;
    [JsonProperty("物品进箱范围", Order = 5)]
    public int HopperRange { get; set; } = 32;
    [JsonProperty("物品进箱冷却", Order = 6)]
    public int HopperCooldown { get; set; } = 60;
    [JsonProperty("物品进箱说明", Order = 7)]
    public string Text { get; set; } = "第一次种植需手动放种子到种植盆,给箱子连上电线与计时器,设置好【进箱范围】即可实现自动化种植";
    #endregion

    #region 预设参数方法
    public void SetDefault()
    {

    }
    #endregion

    #region 读取与创建配置文件方法
    public void Write()
    {
        string json = JsonConvert.SerializeObject(this, Formatting.Indented);
        File.WriteAllText(Paths, json);
    }
    public static Configuration Read()
    {
        if (!File.Exists(Paths))
        {
            var NewConfig = new Configuration();
            NewConfig.SetDefault();
            NewConfig.Write();
            return NewConfig;
        }
        else
        {
            string jsonContent = File.ReadAllText(Paths);
            var config = JsonConvert.DeserializeObject<Configuration>(jsonContent)!;
            return config;
        }
    }
    #endregion
}