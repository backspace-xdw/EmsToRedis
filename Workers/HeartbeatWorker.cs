using System;
using System.Threading;
using System.Threading.Tasks;
using EmsToRedis.Configuration;
using EmsToRedis.Redis;
using NLog;

namespace EmsToRedis.Workers
{
    /// <summary>
    /// 心跳：协议 §6 + §8.7。
    /// 每 HeartbeatIntervalMs 写一次 vfsmp:adapter:heartbeat，带 EX HeartbeatTtlSeconds。
    ///
    /// 注意（协议 §8.6）：
    ///   "全局读 RTDB 失败 → 不写 heartbeat"
    ///   ——所以这里检查 SyncWorker.IsHealthy，false 时跳过本轮心跳，
    ///   让 EAP 通过心跳过期感知到适配器异常。
    /// </summary>
    internal class HeartbeatWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly AdapterConfig _config;
        private readonly RedisWriter _writer;
        private readonly SyncWorker _syncWorker;

        public HeartbeatWorker(AdapterConfig config, RedisWriter writer, SyncWorker syncWorker)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _syncWorker = syncWorker ?? throw new ArgumentNullException(nameof(syncWorker));
        }

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            Log.Info("HeartbeatWorker 启动，间隔 {0} ms，TTL {1} s",
                _config.Adapter.HeartbeatIntervalMs,
                _config.Adapter.HeartbeatTtlSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_syncWorker.IsHealthy)
                    {
                        long nowMs = Logic.TimeHelper.NowUnixMs();
                        await _writer.WriteHeartbeatAsync(nowMs, _config.Adapter.HeartbeatTtlSeconds)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        Log.Debug("数据源异常，跳过本次心跳（让 EAP 自然检测）");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "心跳写入失败，等下一轮");
                }

                try
                {
                    await Task.Delay(_config.Adapter.HeartbeatIntervalMs, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Log.Info("HeartbeatWorker 已停止");
        }
    }
}
