using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// ，按 VFSMP 协议把变化经 IBatch 一次 pipeline 写入 Redis。
    ///
    /// 性能要点：
    ///   * 一轮内全部 HSET / SADD / SREM / DEL 走 IBatch，单次网络往返
    ///   * vehicles:active 用本地 HashSet 镜像，启动时同步一次，之后纯内存维护
    ///   * 每轮 Stopwatch 计时，超过 PollIntervalMs 触发 WARN
    ///   * Task.Delay 用 max(0, interval - elapsed) 抵消周期漂移
    ///
    /// 健康度：volatile bool IsHealthy 暴露给 HeartbeatWorker 决定是否写心跳。
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

        /// <summary>Redis vehicles:active 的本地镜像，避免每轮 SMEMBERS</summary>
        private HashSet<string> _activeIds = new HashSet<string>();

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

            // 启动时同步一次 active set（之后用本地镜像维护）
            try
            {
                _activeIds = await _writer.GetActiveSetAsync().ConfigureAwait(false);
                Log.Info("Redis vehicles:active 镜像加载完成：{0} 辆", _activeIds.Count);
            }
            catch (Exception ex)
            {
                Log.Warn(ex, "加载 vehicles:active 失败，本地镜像置空；冷启动会全量重写");
                _activeIds = new HashSet<string>();
            }

            var sw = new Stopwatch();
            while (!stoppingToken.IsCancellationRequested)
            {
                sw.Restart();
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
                sw.Stop();

                int interval = _config.Adapter.PollIntervalMs;
                long elapsed = sw.ElapsedMilliseconds;
                if (elapsed > interval)
                {
                    Log.Warn("本轮耗时 {0} ms 超过周期 {1} ms，主循环正在漂移", elapsed, interval);
                }
                int delay = (int)Math.Max(0, interval - elapsed);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
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
            var swRead = Stopwatch.StartNew();

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
            IsHealthy = true;
            swRead.Stop();
            long readMs = swRead.ElapsedMilliseconds;

            var swCompute = Stopwatch.StartNew();

            // 2. 统一计算告警 + 时间戳（RtError 车不参与，保留上轮数据）
            //    - 每个灭火器：AlarmRule 判定
            //    - 整车 IsAlarm：任一灭火器告警即整车告警（覆盖 Acquirer 传入值）
            long nowMs = TimeHelper.NowUnixMs();
            int rtErrorCount = 0;
            foreach (var v in current)
            {
                if (v == null) continue;
                if (v.RtError) { rtErrorCount++; continue; }
                v.UpdatedAtUnixMs = nowMs;
                bool anyBoxAlarm = false;
                if (v.FireExtinguishers != null)
                {
                    foreach (var fe in v.FireExtinguishers)
                    {
                        fe.IsAlarm = AlarmRule.IsExtinguisherAlarm(fe);
                        if (fe.IsAlarm) anyBoxAlarm = true;
                    }
                }
                v.IsAlarm = anyBoxAlarm;
            }

            // 3. 当前车辆 id 集合（含 RtError 车，避免被 SREM）
            var currentIds = new HashSet<string>();
            foreach (var v in current)
            {
                if (v != null && !string.IsNullOrEmpty(v.DeviceId))
                    currentIds.Add(v.DeviceId);
            }

            // 4. 冷启动：第一次全量重写一遍（协议 §8.5）
            if (!_coldStartDone)
            {
                swCompute.Stop();
                long computeMsCold = swCompute.ElapsedMilliseconds;
                var swRedisCold = Stopwatch.StartNew();
                await ColdStartAsync(current, currentIds).ConfigureAwait(false);
                swRedisCold.Stop();
                Log.Info("[冷启动] 车 {0} | 读 {1}ms 计算 {2}ms 写 Redis {3}ms",
                    current.Count, readMs, computeMsCold, swRedisCold.ElapsedMilliseconds);
                return;
            }

            // 5. 常规轮：分桶
            //    removes : 上轮 active 中 - 本轮缺失
            //    upserts : 本轮存在 + 字段有变
            //    newActives : 本轮 upsert 中本地镜像未含的（需要 SADD）
            var removes = new List<string>();
            foreach (var id in _activeIds)
            {
                if (!currentIds.Contains(id)) removes.Add(id);
            }

            var upserts = new List<VehicleSnapshot>();
            var newActives = new List<string>();
            var timestampOnly = new List<VehicleSnapshot>();
            foreach (var v in current)
            {
                if (v == null || string.IsNullOrEmpty(v.DeviceId)) continue;
                if (v.RtError) continue; // 本轮采集失败的车保留上轮 Redis 数据
                if (_diff.HasChanged(v))
                {
                    upserts.Add(v);
                    if (!_activeIds.Contains(v.DeviceId)) newActives.Add(v.DeviceId);
                }
                else if (_activeIds.Contains(v.DeviceId))
                {
                    // 字段没变但车还在线：只刷 updatedAt，告诉 EAP 数据仍是最新的
                    timestampOnly.Add(v);
                }
            }

            swCompute.Stop();
            long computeMs = swCompute.ElapsedMilliseconds;

            if (removes.Count == 0 && upserts.Count == 0 && timestampOnly.Count == 0)
            {
                LogCycle(readMs, computeMs, 0, current.Count, 0, 0, 0, 0, rtErrorCount, nowMs);
                return;
            }

            // 6. 一次 IBatch 全部下发
            var swRedis = Stopwatch.StartNew();
            await _writer.ApplyBatchAsync(upserts, newActives, removes, timestampOnly).ConfigureAwait(false);
            swRedis.Stop();

            // 7. 写入成功后更新本地状态
            foreach (var id in removes)
            {
                _activeIds.Remove(id);
                _diff.Forget(id);
            }
            foreach (var v in upserts)
            {
                _diff.Commit(v);
            }
            foreach (var id in newActives)
            {
                _activeIds.Add(id);
            }

            LogCycle(readMs, computeMs, swRedis.ElapsedMilliseconds,
                current.Count, upserts.Count, newActives.Count, removes.Count, timestampOnly.Count,
                rtErrorCount, nowMs);
        }

        /// <summary>
        /// 一轮统一日志：默认 DEBUG（无写入或量小时），有移除/告警异常时升 INFO。
        /// 大批量（1300 车）部署的运维核心可观测点。
        /// updatedAt 列展示本轮写入 Redis 的时间戳（HH:mm:ss.fff + epoch ms），
        /// 用于在没有真实数据变化时也能直观确认时间在走、tsOnly 在生效。
        /// </summary>
        private static void LogCycle(long readMs, long computeMs, long redisMs,
            int totalCars, int upserts, int newActives, int removes, int tsOnly, int rtErrors,
            long updatedAtMs)
        {
            var dt = TimeHelper.FromUnixMsLocal(updatedAtMs);
            string line = string.Format(
                "cycle: 车 {0} | 读 {1}ms 计 {2}ms 写 {3}ms | upsert {4} (新 {5}) ts {6} remove {7} rtError {8} | updatedAt {9:HH:mm:ss.fff} ({10})",
                totalCars, readMs, computeMs, redisMs,
                upserts, newActives, tsOnly, removes, rtErrors,
                dt, updatedAtMs);
            if (removes > 0 || rtErrors > 0) Log.Info(line);
            else Log.Debug(line);
        }

        /// <summary>
        /// 冷启动：把本地镜像清空，强制全量重写一遍，全部成功后才置 _coldStartDone。
        /// </summary>
        private async Task ColdStartAsync(IList<VehicleSnapshot> current, HashSet<string> currentIds)
        {
            Log.Info("冷启动：执行全量重写，车辆数 {0}（已存在镜像 {1}）", current.Count, _activeIds.Count);

            // 把镜像中存在但本轮已经没有的车清掉
            var removes = new List<string>();
            foreach (var id in _activeIds)
            {
                if (!currentIds.Contains(id)) removes.Add(id);
            }

            // 全部车强制重写（不走 diff；RtError 车跳过，保留可能的旧数据）
            var upserts = new List<VehicleSnapshot>();
            var newActives = new List<string>();
            foreach (var v in current)
            {
                if (v == null || string.IsNullOrEmpty(v.DeviceId)) continue;
                if (v.RtError) continue;
                upserts.Add(v);
                if (!_activeIds.Contains(v.DeviceId)) newActives.Add(v.DeviceId);
            }

            await _writer.ApplyBatchAsync(upserts, newActives, removes).ConfigureAwait(false);

            // 全部成功后才推进状态
            foreach (var id in removes)
            {
                _activeIds.Remove(id);
                _diff.Forget(id);
            }
            foreach (var v in upserts)
            {
                _activeIds.Add(v.DeviceId);
                _diff.Commit(v);
            }
            _coldStartDone = true;
            Log.Info("冷启动完成：upsert {0}，remove {1}", upserts.Count, removes.Count);
        }
    }
}
