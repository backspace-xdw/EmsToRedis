using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using EmsToRedis.Models;
using Newtonsoft.Json;

namespace EmsToRedis.Logic
{
    /// <summary>
    /// 内存上一轮快照摘要 + diff 计算（VFSMP 协议 §8.3）。
    ///
    /// 摘要计算时<b>不包含 UpdatedAtUnixMs</b>——
    /// 否则每轮时间戳变化都会被当成"变化"，违反"仅状态变化时才写 Redis"的约定。
    /// </summary>
    internal class DiffTracker
    {
        private readonly Dictionary<string, string> _lastDigest = new Dictionary<string, string>();

        /// <summary>
        /// 判断 v 相比上一轮是否有字段变化。
        /// 首次出现的车辆视为"有变化"，会被首轮写入。
        /// </summary>
        public bool HasChanged(VehicleSnapshot v)
        {
            if (v == null || string.IsNullOrEmpty(v.DeviceId)) return false;
            var digest = ComputeDigest(v);
            string last;
            if (!_lastDigest.TryGetValue(v.DeviceId, out last)) return true;
            return !string.Equals(last, digest);
        }

        /// <summary>
        /// 写入成功后调用，更新上一轮摘要。
        /// </summary>
        public void Commit(VehicleSnapshot v)
        {
            if (v == null || string.IsNullOrEmpty(v.DeviceId)) return;
            _lastDigest[v.DeviceId] = ComputeDigest(v);
        }

        /// <summary>车辆被删除时清理</summary>
        public void Forget(string deviceId)
        {
            if (!string.IsNullOrEmpty(deviceId))
                _lastDigest.Remove(deviceId);
        }

        /// <summary>清空全部缓存（冷启动 / 主动重置时使用）</summary>
        public void Reset()
        {
            _lastDigest.Clear();
        }

        public int Count => _lastDigest.Count;

        private static string ComputeDigest(VehicleSnapshot v)
        {
            // 复制一份避免污染原对象，把 UpdatedAtUnixMs 清零再序列化
            var copy = new VehicleSnapshot
            {
                DeviceId = v.DeviceId,
                Vin = v.Vin,
                PlateNumber = v.PlateNumber,
                Lng = v.Lng,
                Lat = v.Lat,
                Altitude = v.Altitude,
                LocationStatus = v.LocationStatus,
                IsAlarm = v.IsAlarm,
                RtError = v.RtError,
                FireExtinguishers = v.FireExtinguishers,
                UpdatedAtUnixMs = 0
            };
            var json = JsonConvert.SerializeObject(copy);
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(json));
                var sb = new StringBuilder(32);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
