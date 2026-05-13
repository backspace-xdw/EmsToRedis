using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmsToRedis.Models;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace EmsToRedis.Redis
{
    /// <summary>
    /// 所有 Redis 写操作封装。严格遵守 VFSMP 协议 §7 的原子性 / 顺序约定。
    ///
    /// 调用者不需要懂 Redis 命令，只需要：
    ///   * EnsureSchemaVersionAsync —— 启动一次
    ///   * UpsertVehicleAsync       —— 单车写入（内部一次 HSET 多字段）
    ///   * EnsureActiveAsync        —— 若 deviceId 不在 active set 则 SADD
    ///   * RemoveVehicleAsync       —— 先 SREM 后 DEL（顺序敏感）
    ///   * WriteHeartbeatAsync      —— 心跳 SET ... EX 10
    ///   * GetActiveSetAsync        —— 启动 / 每轮对比用
    /// </summary>
    internal class RedisWriter
    {
        private readonly IDatabase _db;

        public RedisWriter(IDatabase db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// 写入 schema:version，启动时调用一次。
        /// </summary>
        public Task EnsureSchemaVersionAsync(string version)
        {
            return _db.StringSetAsync(VfsmpKeys.SchemaVersion, version);
        }

        /// <summary>
        /// 单车 upsert：一次 HSET 写入全部字段（原子）。
        /// 协议 §7：单车原子性 —— 禁止多次 HSET 拼一辆车。
        /// </summary>
        public Task UpsertVehicleAsync(VehicleSnapshot v)
        {
            if (v == null || string.IsNullOrEmpty(v.DeviceId))
                throw new ArgumentException("VehicleSnapshot.DeviceId 不能为空", nameof(v));
            return _db.HashSetAsync(VfsmpKeys.Vehicle(v.DeviceId), BuildHashEntries(v));
        }

        /// <summary>
        /// 把 deviceId 加入 vehicles:active SET（幂等）。
        /// 协议 §7：先 HSET vehicle，再 SADD active。
        /// </summary>
        public Task EnsureActiveAsync(string deviceId)
        {
            return _db.SetAddAsync(VfsmpKeys.VehiclesActive, deviceId);
        }

        /// <summary>
        /// 车辆软删：先 SREM active，再 DEL vehicle:{id}。
        /// 协议 §7：删除顺序敏感。
        /// </summary>
        public async Task RemoveVehicleAsync(string deviceId)
        {
            await _db.SetRemoveAsync(VfsmpKeys.VehiclesActive, deviceId).ConfigureAwait(false);
            await _db.KeyDeleteAsync(VfsmpKeys.Vehicle(deviceId)).ConfigureAwait(false);
        }

        /// <summary>
        /// 读 vehicles:active 全集。供启动对比和每轮增删感知用。
        /// </summary>
        public async Task<HashSet<string>> GetActiveSetAsync()
        {
            var values = await _db.SetMembersAsync(VfsmpKeys.VehiclesActive).ConfigureAwait(false);
            var result = new HashSet<string>();
            foreach (var v in values)
            {
                if (!v.IsNull) result.Add(v.ToString());
            }
            return result;
        }

        /// <summary>
        /// 批量写入：一次 IBatch 把所有 upsert/active/remove 命令 pipeline 出去。
        /// 协议 §7 顺序要求：
        ///   * 单车 upsert：先 HSET vehicle，再 SADD active —— 在 IBatch 内按此顺序入队
        ///   * 单车 remove：先 SREM active，再 DEL vehicle —— 同上
        /// 跨车顺序无约束。返回前 await 所有命令的 Task。
        /// </summary>
        /// <param name="upserts">本轮需要写入的车辆快照</param>
        /// <param name="newActives">本轮新加入 active 集合的 deviceId（旧的不必重复 SADD）</param>
        /// <param name="removes">本轮要清掉的 deviceId</param>
        public async Task ApplyBatchAsync(
            IList<VehicleSnapshot> upserts,
            IList<string> newActives,
            IList<string> removes)
        {
            if ((upserts == null || upserts.Count == 0)
                && (newActives == null || newActives.Count == 0)
                && (removes == null || removes.Count == 0))
            {
                return;
            }

            var batch = _db.CreateBatch();
            var pending = new List<Task>(
                (upserts?.Count ?? 0) + (newActives?.Count ?? 0) + ((removes?.Count ?? 0) * 2));

            // 先 remove（先 SREM 再 DEL，单车顺序敏感）
            if (removes != null)
            {
                foreach (var id in removes)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    pending.Add(batch.SetRemoveAsync(VfsmpKeys.VehiclesActive, id));
                    pending.Add(batch.KeyDeleteAsync(VfsmpKeys.Vehicle(id)));
                }
            }

            // 再 upsert：HSET 先入队（保证 §7 单车顺序）
            if (upserts != null)
            {
                foreach (var v in upserts)
                {
                    if (v == null || string.IsNullOrEmpty(v.DeviceId)) continue;
                    pending.Add(batch.HashSetAsync(VfsmpKeys.Vehicle(v.DeviceId), BuildHashEntries(v)));
                }
            }

            // 后 SADD active（新出现的车）
            if (newActives != null)
            {
                foreach (var id in newActives)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    pending.Add(batch.SetAddAsync(VfsmpKeys.VehiclesActive, id));
                }
            }

            batch.Execute();
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        /// <summary>
        /// 心跳：SET vfsmp:adapter:heartbeat unixMs EX ttlSeconds。
        /// </summary>
        public Task WriteHeartbeatAsync(long unixMs, int ttlSeconds)
        {
            return _db.StringSetAsync(VfsmpKeys.Heartbeat,
                unixMs.ToString(),
                TimeSpan.FromSeconds(ttlSeconds));
        }

        private static HashEntry[] BuildHashEntries(VehicleSnapshot v)
        {
            var fireExtJson = JsonConvert.SerializeObject(
                v.FireExtinguishers ?? new List<FireExtinguisher>());
            return new HashEntry[]
            {
                new HashEntry("deviceId", v.DeviceId),
                new HashEntry("lng", v.Lng ?? "0"),
                new HashEntry("lat", v.Lat ?? "0"),
                new HashEntry("altitude", v.Altitude ?? "0"),
                new HashEntry("locationStatus", v.LocationStatus ?? "0"),
                new HashEntry("isAlarm", v.IsAlarm ? "1" : "0"),
                new HashEntry("fireExtinguishers", fireExtJson),
                new HashEntry("updatedAt", v.UpdatedAtUnixMs.ToString())
            };
        }
    }
}
