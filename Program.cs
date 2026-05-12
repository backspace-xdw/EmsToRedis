using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmsToRedis.Configuration;
using EmsToRedis.Redis;
using EmsToRedis.Workers;
using NLog;
using StackExchange.Redis;

namespace EmsToRedis
{
    /// <summary>
    /// VFSMP 9902 同机适配器入口。
    /// 流程：
    ///   1. 加载 appsettings.json
    ///   2. 连接 Redis
    ///   3. 启动 SyncWorker（1s 轮询写）+ HeartbeatWorker（3s 心跳）
    ///   4. 等待 Ctrl+C / 进程结束
    /// </summary>
    internal class Program
    {
        private static readonly Logger Log = LogManager.GetCurrentClassLogger();

        private static int Main(string[] args)
        {
            try
            {
                Run(args);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "EmsToRedis 致命错误，进程退出");
                return 1;
            }
            finally
            {
                LogManager.Shutdown();
            }
        }

        private static void Run(string[] args)
        {
            Log.Info("=========================================");
            Log.Info(" EmsToRedis (VFSMP 9902 Adapter) 启动中");
            Log.Info("=========================================");

            // 1. 加载配置
            string configPath = ResolveConfigPath(args);
            var config = AdapterConfig.Load(configPath);
            Log.Info("配置文件：{0}", configPath);
            Log.Info("Schema 版本：{0}", config.Adapter.SchemaVersion);
            Log.Info("轮询间隔：{0} ms / 心跳：{1} ms (TTL {2}s)",
                config.Adapter.PollIntervalMs,
                config.Adapter.HeartbeatIntervalMs,
                config.Adapter.HeartbeatTtlSeconds);

            // 2. 连接 Redis（同步阻塞，直到连上或抛错）
            ConnectionMultiplexer mux;
            try
            {
                mux = ConnectionMultiplexer.Connect(config.Redis.ConnectionString);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法连接 Redis：" + config.Redis.ConnectionString, ex);
            }
            Log.Info("Redis 已连接");

            // 注册连接事件日志（可选，便于排错）
            mux.ConnectionFailed += (s, e) =>
                Log.Warn("Redis 连接失败 endpoint={0} type={1}: {2}",
                    e.EndPoint, e.ConnectionType, e.Exception?.Message);
            mux.ConnectionRestored += (s, e) =>
                Log.Info("Redis 连接恢复 endpoint={0} type={1}",
                    e.EndPoint, e.ConnectionType);

            using (mux)
            {
                var db = mux.GetDatabase();
                var writer = new RedisWriter(db);

                // 3. 启动两个 Worker
                var sync = new SyncWorker(config, writer);
                var heartbeat = new HeartbeatWorker(config, writer, sync);

                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    Log.Info("收到 Ctrl+C，正在停止...");
                    cts.Cancel();
                };

                var syncTask = Task.Run(() => sync.RunAsync(cts.Token));
                var hbTask = Task.Run(() => heartbeat.RunAsync(cts.Token));

                Log.Info("启动完成，进入运行状态。Ctrl+C 退出。");

                try
                {
                    Task.WaitAll(new[] { syncTask, hbTask });
                }
                catch (AggregateException ex)
                {
                    foreach (var inner in ex.InnerExceptions)
                    {
                        if (!(inner is OperationCanceledException))
                            Log.Error(inner, "Worker 异常退出");
                    }
                }
            }

            Log.Info("EmsToRedis 已退出");
        }

        /// <summary>
        /// 解析 appsettings.json 位置：
        /// 优先命令行参数 -c &lt;path&gt;，否则程序目录下的 appsettings.json。
        /// </summary>
        private static string ResolveConfigPath(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-c" || args[i] == "--config")
                {
                    return args[i + 1];
                }
            }
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "appsettings.json");
        }
    }
}
