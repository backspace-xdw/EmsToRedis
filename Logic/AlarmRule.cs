using EmsToRedis.Models;

namespace EmsToRedis.Logic
{
    /// <summary>
    /// 灭火器告警判定（VFSMP 协议 §8.4）。
    ///
    /// 当前为占位实现，恒返回 false。
    /// 拿到 EAP 团队的 AlarmRule.IsExtinguisherAlarm C# 源码后，
    /// 直接替换 IsExtinguisherAlarm 方法体即可。
    /// </summary>
    public static class AlarmRule
    {
        /// <summary>
        /// 判定单个灭火器是否处于告警状态。
        /// </summary>
        /// <remarks>
        /// 占位逻辑：永远返回 false。
        /// 后续需复制 EAP 项目里的 IsExtinguisherAlarm 实现。
        /// </remarks>
        public static bool IsExtinguisherAlarm(FireExtinguisher fe)
        {
            if (fe == null) return false;

            // ─── TODO: 替换为 EAP 提供的真实判定逻辑 ────────────────────
            // 示例（伪代码）：
            //   return fe.WarningLevel != "0"
            //       || fe.FireAlarmLevel != "0"
            //       || fe.CustomFaultCode != "0";
            // ───────────────────────────────────────────────────────────

            return false;
        }
    }
}
