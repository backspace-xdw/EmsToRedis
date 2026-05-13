using System.Collections.Generic;
using EmsToRedis.Models;

namespace EmsToRedis.Logic
{
    /// <summary>
    /// 内存上一轮快照摘要 + diff 计算（VFSMP 协议 §8.3）。
    ///
    /// 实现：FNV-1a 64 位增量哈希，直接吃字段字符串/bool/int，
    /// 不走 JSON 序列化和 MD5，相比 1300 车场景每秒 2MB+ GC 压力降到 ~0。
    ///
    /// 摘要计算时不包含 UpdatedAtUnixMs / RtError，否则会污染 diff 结果。
    /// </summary>
    internal class DiffTracker
    {
        private readonly Dictionary<string, ulong> _lastDigest = new Dictionary<string, ulong>();

        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>
        /// 判断 v 相比上一轮是否有字段变化。
        /// 首次出现的车辆视为"有变化"，会被首轮写入。
        /// </summary>
        public bool HasChanged(VehicleSnapshot v)
        {
            if (v == null || string.IsNullOrEmpty(v.DeviceId)) return false;
            ulong digest = ComputeDigest(v);
            ulong last;
            if (!_lastDigest.TryGetValue(v.DeviceId, out last)) return true;
            return last != digest;
        }

        /// <summary>写入成功后调用，更新上一轮摘要。</summary>
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

        private static ulong ComputeDigest(VehicleSnapshot v)
        {
            ulong h = FnvOffset;
            h = HashString(h, v.DeviceId);
            h = HashString(h, v.Lng);
            h = HashString(h, v.Lat);
            h = HashString(h, v.Altitude);
            h = HashString(h, v.LocationStatus);
            h = HashBool(h, v.IsAlarm);
            // UpdatedAtUnixMs / RtError 不参与
            if (v.FireExtinguishers != null)
            {
                foreach (var fe in v.FireExtinguishers)
                {
                    if (fe == null) { h = MixByte(h, 0); continue; }
                    h = HashString(h, fe.BoxNumber);
                    h = HashString(h, fe.StartType);
                    h = HashString(h, fe.WarningLevel);
                    h = HashString(h, fe.Status);
                    h = HashString(h, fe.Command);
                    h = HashString(h, fe.CustomFaultCode);
                    h = HashString(h, fe.FireAlarmLevel);
                    h = HashBool(h, fe.IsAlarm);
                }
            }
            return h;
        }

        private static ulong HashString(ulong h, string s)
        {
            if (s == null) return MixByte(h, 0);
            for (int i = 0; i < s.Length; i++)
            {
                int c = s[i];
                h = MixByte(h, (byte)(c & 0xFF));
                h = MixByte(h, (byte)((c >> 8) & 0xFF));
            }
            return MixByte(h, 1); // 分隔符，防止 "a"+"b" 与 "ab" 撞哈希
        }

        private static ulong HashBool(ulong h, bool b)
        {
            return MixByte(h, b ? (byte)0xA1 : (byte)0xB2);
        }

        private static ulong MixByte(ulong h, byte b)
        {
            h ^= b;
            h *= FnvPrime;
            return h;
        }
    }
}
