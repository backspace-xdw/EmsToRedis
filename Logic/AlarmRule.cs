using System.Globalization;
using EmsToRedis.Models;

namespace EmsToRedis.Logic
{
    /// <summary>
    /// 灭火器告警判定（VFSMP 协议 §8.4）。
    /// 与 EAP 后端 backend/PlatformService/Helpers/AlarmRule.cs 口径一致：
    ///
    ///   isAlarm =  (2 &lt;= startType &lt;= 7) || startType == 255   // 已发射 / 探测器无可用灭火器
    ///           || (1 &lt;= warningLevel &lt;= 4)                     // 一~四级预警
    ///           || (1 &lt;= status &lt;= 3)                           // 已启动 / 传感器故障 / 硬件故障
    ///           || (customFaultCode == 1 || == 2)               // 编码故障 / 厂家硬件故障
    ///           || (fireAlarmLevel &gt; 0)                         // 任意火警等级即告警
    ///
    /// 不参与告警：command（子阀命令，仅展示）
    /// 字段解析失败按 0 处理，不告警。
    /// </summary>
    public static class AlarmRule
    {
        public static bool IsExtinguisherAlarm(FireExtinguisher fe)
        {
            if (fe == null) return false;

            int startType = ParseOrZero(fe.StartType);
            if ((startType >= 2 && startType <= 7) || startType == 255) return true;

            int warningLevel = ParseOrZero(fe.WarningLevel);
            if (warningLevel >= 1 && warningLevel <= 4) return true;

            int status = ParseOrZero(fe.Status);
            if (status >= 1 && status <= 3) return true;

            int customFault = ParseOrZero(fe.CustomFaultCode);
            if (customFault == 1 || customFault == 2) return true;

            int fireAlarmLevel = ParseOrZero(fe.FireAlarmLevel);
            if (fireAlarmLevel > 0) return true;

            return false;
        }

        private static int ParseOrZero(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }
}
