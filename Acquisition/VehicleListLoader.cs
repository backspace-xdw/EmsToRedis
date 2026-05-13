using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NLog;

namespace EmsToRedis.Acquisition
{
    /// <summary>
    /// 读取 DeviceToEmspoint.ini 把车辆列表加载为 List&lt;IDtoNameClass&gt;。
    ///
    /// 文件位置：程序工作目录下 DeviceToEmspoint.ini（与既有 EMS 项目保持一致）。
    /// 编码：GB2312 / GBK（Encoding.Default）。
    ///
    /// 行格式（# 或 ; 开头视为注释）：
    ///   Emspoint,DeviceID
    /// </summary>
    public static class VehicleListLoader
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();
        public const string FileName = "DeviceToEmspoint.ini";

        public static List<IDtoNameClass> Load(string baseDir = null)
        {
            if (string.IsNullOrEmpty(baseDir))
                baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, FileName);
            var list = new List<IDtoNameClass>();

            if (!File.Exists(path))
            {
                Log.Warn("车辆列表文件不存在：{0}（启动时不会有任何车辆被采集）", path);
                return list;
            }

            string[] lines = File.ReadAllLines(path, Encoding.Default);
            int lineNo = 0;
            foreach (var raw in lines)
            {
                lineNo++;
                string line = raw == null ? "" : raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                string[] cols = line.Split(',');
                if (cols.Length < 2)
                {
                    Log.Warn("跳过 {0} 第 {1} 行（字段数 < 2）：{2}", FileName, lineNo, line);
                    continue;
                }

                string emspoint = cols[0].Trim();
                string deviceId = cols[1].Trim();
                if (emspoint.Length == 0 || deviceId.Length == 0)
                {
                    Log.Warn("跳过 {0} 第 {1} 行（Emspoint 或 DeviceID 为空）", FileName, lineNo);
                    continue;
                }

                list.Add(new IDtoNameClass
                {
                    Emspoint = emspoint,
                    DeviceID = deviceId,
                });
            }

            Log.Info("车辆列表加载完成：{0} 辆（{1}）", list.Count, path);
            return list;
        }
    }
}
