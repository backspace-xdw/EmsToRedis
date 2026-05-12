using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmsToRedis.Acquisition;
using EmsToRedis.Configuration;
using EmsToRedis.Logic;
using EmsToRedis.Models;
using EmsToRedis.Redis;
using NLog;

namespace EmsToRedis.Workers
{
    /// <summary>
    /// 主循环：每 PollIntervalMs 调用一次 VehicleAcquirer.ReadAllAsync
    /// ，按 VFSMP 协议把变化写入 Redis。
    ///
    /// 同时维护一个 volatile bool IsHealthy，供 HeartbeatWorker 决定是否继续发心跳。
    /// </summary>
    internal class SyncWorker
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private readonly AdapterConfig _config;
        private readonly RedisWriter _writer;
        private readonly DiffTracker _diff;

        /// <summary>
        /// 数据源是否健康。HeartbeatWorker 检查此标志决定是否写心跳。
        /// - true：本轮 ReadAllAsync 成功
        /// - false：异常或首次未执行
        /// </summary>
        public volatile bool IsHealthy = false;

        /// <summary>是否已经完成首次"冷启动全量重写"（协议 §8.5）</summary>
        private bool _coldStartDone = false;

        public SyncWorker(AdapterConfig config, RedisWriter writer)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _diff = new DiffTracker();
        }

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            Log.Info("SyncWorker 启动，间隔 {0} ms", _config.Adapter.PollIntervalMs);

            // 启动时写 schema:version（协议 §2）
            try
            {
                await _writer.EnsureSchemaVersionAsync(_config.Adapter.SchemaVersion).ConfigureAwait(false);
                Log.Info("schema:version 已写入：{0}", _config.Adapter.SchemaVersion);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "写 schema:version 失败，本轮将重试");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await DoOneCycleAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (NotImplementedException nie)
                {
                    // VehicleAcquirer 未实现，单独提示一下避免日志污染
                    IsHealthy = false;
                    Log.Error("VehicleAcquirer.ReadAllAsync 未实现：{0}", nie.Message);
                }
                catch (Exception ex)
                {
                    IsHealthy = false;
                    Log.Error(ex, "本轮采集/写入失败，等下一轮");
                }

                try
                {
                    await Task.Delay(_config.Adapter.PollIntervalMs, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Log.Info("SyncWorker 已停止");
        }

        private async Task DoOneCycleAsync(CancellationToken ct)
        {
            // 1. 拉数据
            IList<VehicleSnapshot> current;
            try
            {
                current = await VehicleAcquirer.ReadAllAsync(ct).ConfigureAwait(false);
                if (current == null) current = new List<VehicleSnapshot>();
            }
            catch
            {
                IsHealthy = false;
                throw;
            }

            // 数据源读取成功，标记健康（即便没车也算正常）
            IsHealthy = true;

            // 2. 统一计算 IsAlarm + 时间戳
            long nowMs = TimeHelper.NowUnixMs();
            foreach (var v in current)
            {
                if (v == null) continue;
                v.UpdatedAtUnixMs = nowMs;
                if (v.FireExtinguishers != null)
                {
                    foreach (var fe in v.FireExtinguishers)
                    {
                        fe.IsAlarm = AlarmRule.IsExtinguisherAlarm(fe);
                    }
                }
            }

            // 3. 取当前 Redis 中的 active set，用于增删感知
            HashSet<string> activeNow = await _writer.GetActiveSetAsync().ConfigureAwait(false);

            // 4. 准备当前车辆 id 集合
            var currentIds = new HashSet<string>();
            foreach (var v in current)
            {
                if (v != null && !string.IsNullOrEmpty(v.DeviceId))
                    currentIds.Add(v.DeviceId);
            }

            // 5. 冷启动：第一次全量重写一遍（协议 §8.5）
            if (!_coldStartDone)
            {
                Log.Info("冷启动：执行全量重写，车辆数 {0}", current.Count);

                // 把 Redis 中存在但本轮已经没有的车清掉
                foreach (var gone in DiffSet(activeNow, currentIds))
                {
                    await _writer.RemoveVehicleAsync(gone).ConfigureAwait(false);
                    _diff.Forget(gone);
                }

                // 全部车强制重写一次（无 diff）
                foreach (var v in current)
                {
                    if (v == null || string.IsNullOrEmpty(v.DeviceId)) continue;
                    await _writer.UpsertVehicleAsync(v).ConfigureAwait(false);
                    await _writer.EnsureActiveAsync(v.DeviceId).ConfigureAwait(false);
                    _diff.Commit(v);
                }

                _coldStartDone = true;
                return;
            }

            // 6. 常规轮：增删感知
            int removedCount = 0;
            foreach (var gone in DiffSet(activeNow, currentIds))
            {
                await _writer.RemoveVehicleAsync(gone).ConfigureAwait(false);
                _diff.Forget(gone);
                removedCount++;
            }
            if (removedCount > 0)
                Log.Info("移除 {0} 辆车（Redis 中存在但本轮已不在）", removedCount);

            // 7. 常规轮：diff 写入
            int writtenCount = 0;
            foreach (var v in current)
            {
                if (v == null || string.IsNullOrEmpty(v.DeviceId)) continue;
                if (_diff.HasChanged(v))
                {
                    await _writer.UpsertVehicleAsync(v).ConfigureAwait(false);
                    if (!activeNow.Contains(v.DeviceId))
                        await _writer.EnsureActiveAsync(v.DeviceId).ConfigureAwait(false);
                    _diff.Commit(v);
                    writtenCount++;
                }
            }
            if (writtenCount > 0)
                Log.Debug("本轮写入 {0} 辆车（共 {1} 辆）", writtenCount, current.Count);
        }

        private static IEnumerable<string> DiffSet(HashSet<string> a, HashSet<string> b)
        {
            // a - b
            var result = new List<string>();
            foreach (var x in a)
            {
                if (!b.Contains(x)) result.Add(x);
            }
            return result;
        }
    }
}
