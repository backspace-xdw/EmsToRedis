using System;
using System.IO;
using Newtonsoft.Json;

namespace EmsToRedis.Configuration
{
    /// <summary>
    /// appsettings.json 强类型映射。
    /// 通过 AdapterConfig.Load(path) 静态方法加载。
    /// </summary>
    public class AdapterConfig
    {
        public RedisSection Redis { get; set; } = new RedisSection();
        public AdapterSection Adapter { get; set; } = new AdapterSection();

        public static AdapterConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"配置文件不存在：{Path.GetFullPath(path)}。请确认 appsettings.json 已部署到程序目录。");
            }
            var json = File.ReadAllText(path);
            var cfg = JsonConvert.DeserializeObject<AdapterConfig>(json);
            if (cfg == null) throw new InvalidOperationException("appsettings.json 解析为 null");
            cfg.Validate();
            return cfg;
        }

        private void Validate()
        {
            if (string.IsNullOrWhiteSpace(Redis.ConnectionString))
                throw new InvalidOperationException("Redis.ConnectionString 不能为空");
            if (string.IsNullOrWhiteSpace(Adapter.SchemaVersion))
                throw new InvalidOperationException("Adapter.SchemaVersion 不能为空");
            if (Adapter.PollIntervalMs < 100)
                throw new InvalidOperationException("Adapter.PollIntervalMs 不能小于 100");
            if (Adapter.HeartbeatIntervalMs < 500)
                throw new InvalidOperationException("Adapter.HeartbeatIntervalMs 不能小于 500");
            if (Adapter.HeartbeatTtlSeconds < 5)
                throw new InvalidOperationException("Adapter.HeartbeatTtlSeconds 不能小于 5");

            // 协议 §6：TTL > 心跳间隔 × 3，避免单次写失败 / 网络抖动误判
            int hbIntervalSec = Adapter.HeartbeatIntervalMs / 1000;
            if (Adapter.HeartbeatTtlSeconds <= hbIntervalSec * 3)
            {
                throw new InvalidOperationException(
                    $"Adapter.HeartbeatTtlSeconds ({Adapter.HeartbeatTtlSeconds}s) 必须 > HeartbeatIntervalMs ({Adapter.HeartbeatIntervalMs}ms) × 3");
            }
        }
    }

    public class RedisSection
    {
        public string ConnectionString { get; set; } = "";
    }

    public class AdapterSection
    {
        public string SchemaVersion { get; set; } = "1";

        /// <summary>
        /// 主循环采集周期（毫秒）。下限 100ms（Validate 强制），上限自行衡量。
        /// 经验值：
        ///   ≤ 200 车  → 1000ms
        ///   ≤ 1300 车 → 1000~2000ms（GetAxVM 批量读后单轮约 300ms）
        ///   ≥ 3000 车 → 2000~3000ms 并考虑分片或并行
        /// 现场看 SyncWorker 输出的 "cycle: 读 X ms 计 Y ms 写 Z ms"
        /// 若 (X+Y+Z) 持续 > 当前 PollIntervalMs 会触发 WARN，需上调该值。
        /// </summary>
        public int PollIntervalMs { get; set; } = 1000;

        public int HeartbeatIntervalMs { get; set; } = 3000;
        public int HeartbeatTtlSeconds { get; set; } = 10;
        public int VehicleOfflineTimeoutSeconds { get; set; } = 30;
    }
}
