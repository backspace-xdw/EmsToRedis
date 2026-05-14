# EmsToRedis Redis 数据结构说明（订阅方阅读）

> 适用版本：EmsToRedis v1.1 / VFSMP Redis 共享层协议 v1
> 适用对象：从 Redis 读取车辆数据的第三方（EAP / 平台 / 大屏）
> 写入方（本程序）每秒一轮全量采集，**`updatedAt` 每轮都刷新**，其余字段仅在变化时整体 HSET。详见 §6。

---

## 1. 全部 Key 一览

| Key | 类型 | 写入频率 | 是否会过期 | 用途 |
| --- | --- | --- | --- | --- |
| `vfsmp:schema:version` | STRING | 启动时 1 次 | 否 | 协议版本号，读到 `"1"` 即本文档版本 |
| `vfsmp:vehicles:active` | SET | 变化时增量 | 否 | 当前所有"在册"车辆的 deviceId 集合 |
| `vfsmp:vehicle:{deviceId}` | HASH | 每轮刷 `updatedAt`；其余字段变化时刷新 | 否 | 单车实时快照，N 辆车就有 N 个 key |
| `vfsmp:adapter:heartbeat` | STRING | 每 3s 一次 | **TTL 10s** | 适配器心跳，超期视为离线 |

> Redis 命名空间统一前缀 `vfsmp:`，订阅方按需匹配。

---

## 2. `vfsmp:vehicle:{deviceId}` — 单车快照（核心）

**Key 命名**：`vfsmp:vehicle:` + `deviceId`，例如 `vfsmp:vehicle:4G2501080001`
**类型**：HASH，8 个字段
**编码**：所有字段值都是 UTF-8 字符串（含 JSON 内部）

### 2.1 字段定义

| 字段 | 类型 | 示例 | 含义 |
| --- | --- | --- | --- |
| `deviceId` | string | `4G2501080001` | 设备唯一标识，与 key 中 `{deviceId}` 一致 |
| `lng` | string (float) | `121.473701` | 经度（WGS84），6 位小数 |
| `lat` | string (float) | `31.230416` | 纬度（WGS84），6 位小数 |
| `altitude` | string (number) | `10` 或 `12.345` | 海拔（米），整数无小数点 |
| `locationStatus` | string `"1"`/`"0"` | `1` | 定位是否有效：`1`=好点 / `0`=坏点超时 |
| `isAlarm` | string `"1"`/`"0"` | `0` | 整车告警状态，16 个灭火器任一告警即 `1` |
| `fireExtinguishers` | string (JSON) | 见 §3 | 16 个灭火器的实时数据数组 |
| `updatedAt` | string (unix ms) | `1747120410123` | **最近一次成功采集时刻**（写 Redis 的时间），每轮刷新 |

### 2.2 完整示例

```
HGETALL vfsmp:vehicle:4G2501080001
```
返回（顺序无意义）：
```
deviceId        4G2501080001
lng             121.473701
lat             31.230416
altitude        10
locationStatus  1
isAlarm         0
fireExtinguishers  [{"boxNumber":"0", ... }, ...]   ← 见 §3
updatedAt       1747120410123
```

### 2.3 字段语义补充

- **`lng` / `lat` = "0.000000"**：通常表示无定位（GPS 未锁星 / 设备未上传）。配合 `locationStatus="0"` 一起判断。
- **`updatedAt`**：写入方"最近一次成功读到这辆车数据"的时刻（毫秒）。即使其他字段没变化，只要本轮采集到了该车，就会被刷新。订阅方可直接用 `now - updatedAt > 阈值` 判断单车数据陈旧（推荐阈值 = 写入方 `PollIntervalMs` × 3，默认 3000 ms）。
- **`isAlarm` 的派生**：写入方按 §4 规则逐箱判定，**任一箱告警即整车告警**。订阅方一般不需要重算。
- **HSET 原子性**：单车整体刷新时 8 个字段一次 HSET 完成；只刷 `updatedAt` 时是单字段 HSET。两种情况各自原子，订阅方读到的永远是某轮内一致的快照，不会半新半旧。

---

## 3. `fireExtinguishers` 子结构（JSON 数组）

**类型**：JSON 字符串，数组长度恒为 **16**（即每车 16 个灭火器箱）
**索引**：数组下标 `[0..15]`，与字段 `boxNumber` 一致

### 3.1 单元素字段

```json
{
  "boxNumber":        "0",      // 箱号，"0".."15"
  "startType":        "0",      // 启动类型
  "warningLevel":     "0",      // 预警级别
  "status":           "1",      // 灭火器状态
  "command":          "0",      // 子阀命令（仅展示，不参与告警）
  "customFaultCode":  "0",      // 自定义故障码
  "fireAlarmLevel":   "0",      // 火警等级
  "isAlarm":          false     // 本箱是否告警（写入方按 §4 计算）
}
```

> 注意：除 `isAlarm` 是 bool 外，其余字段在 JSON 中**都是字符串**（外层 HASH 字段全是字符串，内部统一风格）。订阅方读 number 时按 `parseInt(str || "0")` 处理。

### 3.2 整体示例（16 个箱号）

```json
[
  {"boxNumber":"0","startType":"0","warningLevel":"0","status":"1","command":"0","customFaultCode":"0","fireAlarmLevel":"0","isAlarm":false},
  {"boxNumber":"1","startType":"0","warningLevel":"0","status":"0","command":"0","customFaultCode":"0","fireAlarmLevel":"0","isAlarm":false},
  ...
  {"boxNumber":"15","startType":"0","warningLevel":"0","status":"0","command":"0","customFaultCode":"0","fireAlarmLevel":"0","isAlarm":false}
]
```

---

## 4. 告警判定规则（订阅方可独立复算）

**每个箱独立判定，4 类 5 条任一命中 ⇒ `isAlarm = true`**：

| 字段 | 触发条件 | 含义 |
| --- | --- | --- |
| `startType` | ∈ [2..7] 或 == 255 | 已发射 / 探测器无可用灭火器（虚拟控制） |
| `warningLevel` | ∈ [1..4] | 一~四级预警 |
| `status` | ∈ [1..3] | 已启动 / 传感器故障 / 硬件故障 |
| `customFaultCode` | == 1 或 == 2 | 编码故障 / 厂家硬件故障 |
| `fireAlarmLevel` | > 0 | 任意火警等级 |

**不参与告警**：`command`（仅展示）、`boxNumber`
**解析失败**：字符串非数字一律按 0 处理（即不告警）

**整车 `isAlarm`** = OR(16 个箱 `isAlarm`)

> 写入方已经算好 `isAlarm` 字段并写入 Redis；订阅方一般直接用。如需自行复算（比如想换告警口径），请按上表实现。源码：`Logic/AlarmRule.cs`。

---

## 5. `vfsmp:vehicles:active` — 在册车辆集合

**类型**：SET，元素为 `deviceId` 字符串
**用途**：订阅方一次性拿到全部车辆 ID，而无需 `SCAN vfsmp:vehicle:*`

### 5.1 常用读取

```
SMEMBERS vfsmp:vehicles:active
# → ["4G2501080001", "4G2501080002", ...]

SCARD vfsmp:vehicles:active
# → 当前在册车辆数
```

### 5.2 增删时机
- **新车出现**（ini 增加一行 + 重启 / 热加载）→ 写入方 `SADD` 加入
- **车辆下架**（ini 删除）→ 写入方 `SREM` 移除 + `DEL vfsmp:vehicle:{id}`
- **单车采集失败**：**不**移除（保持 active 不变；HASH 数据保留上轮，`updatedAt` 停在最后一次成功的时刻）

---

## 6. 写入频率 / Diff 行为（重要）

写入方循环周期 = `PollIntervalMs`（默认 1000 ms，可配）。每轮对每辆车产生 **3 类写入之一**：

| 情况 | Redis 操作 | 频率 |
| --- | --- | --- |
| 字段发生变化（lng/lat/灭火器等任一变） | 整车 8 字段 HSET（含新 `updatedAt`） | 真有变化时触发 |
| 字段无变化但本轮采集成功 | **单字段 HSET `updatedAt`** | 几乎每轮，每车一次 |
| 本轮该车采集失败（`RtError`） | 不写入，HASH 保持上轮，`updatedAt` 冻结 | 异常时 |

**订阅方含义**：

- `updatedAt` 现在是"采集器最近一次成功读到这辆车的时刻"。判断单车存活直接看它的推进就行，不再需要靠心跳间接推断。
- 字段值是否变化看 `updatedAt` **不够**——它每秒都在动；要靠 §9.2 的 keyspace notification、或显式比对字段值。
- **Keyspace 通知会很密**：1300 车 + 1s 周期 ≈ 每秒 1300 个 `hset` 事件。订阅方若只关心"业务字段是否变化"，建议在收到通知后用 HGETALL 拉快照自行比对，或换用周期性轮询而非事件流。

### 启用 keyspace notification（订阅方一次性配置）

```
CONFIG SET notify-keyspace-events Khs
```
`K` = keyspace events，`h` = hash 事件，`s` = set 事件。订阅频道：
```
PSUBSCRIBE '__keyspace@0__:vfsmp:vehicle:*'
```

---

## 7. `vfsmp:adapter:heartbeat` — 心跳

**类型**：STRING + TTL（默认 10s）
**值**：写入时刻的 unix ms（字符串）
**写入频率**：每 `HeartbeatIntervalMs`（默认 3s）

```
GET vfsmp:adapter:heartbeat
# → "1747120410123"

TTL vfsmp:adapter:heartbeat
# → 7   (距离过期还剩 7 秒)
```

### 7.1 订阅方健康判定

```
heartbeat_alive = (GET vfsmp:adapter:heartbeat) ≠ nil
```

- `nil` 或 TTL 过期 ⇒ **适配器异常**（进程死了 / 数据源全断 / Redis 写不上）
  → 这时 `vfsmp:vehicle:*` 里的数据是陈旧数据，订阅方应把整体显示为"采集中断"
- 正常时心跳 unix ms 应每 ~3 秒推进一次

### 7.2 写入方主动停发心跳的情形
- 数据源（EosDapi.dll）整体读失败：跳过本次心跳写
- 单车失败不停心跳（局部异常不影响整体健康度）

---

## 8. `vfsmp:schema:version` — 协议版本

**类型**：STRING
**值**：当前为 `"1"`
**用途**：订阅方启动时读一次，判断本文档是否仍适用

```
GET vfsmp:schema:version
# → "1"
```

后续协议字段变更时此值递增，订阅方据此触发兼容性逻辑或拒绝运行。

---

## 9. 订阅方推荐读取流程

### 9.1 首次启动 / 周期性全量
```python
# 1. 协议版本校验
assert redis.get("vfsmp:schema:version") == "1"

# 2. 适配器健康
assert redis.get("vfsmp:adapter:heartbeat") is not None

# 3. 拉车辆列表
ids = redis.smembers("vfsmp:vehicles:active")

# 4. 批量管道 HGETALL
pipe = redis.pipeline()
for id in ids:
    pipe.hgetall(f"vfsmp:vehicle:{id}")
results = pipe.execute()

# 5. JSON.parse fireExtinguishers
for r in results:
    r["fireExtinguishers"] = json.loads(r["fireExtinguishers"])
```

### 9.2 增量订阅（实时大屏）
```python
# 1. CONFIG SET notify-keyspace-events Khs   （一次性）
# 2. 订阅 hset / sadd / srem 事件
ps = redis.pubsub()
ps.psubscribe("__keyspace@0__:vfsmp:vehicle:*")
ps.psubscribe("__keyspace@0__:vfsmp:vehicles:active")
for msg in ps.listen():
    if msg["type"] == "pmessage":
        key = msg["channel"].decode().split(":", 2)[-1]
        # 收到变化通知 → HGETALL 该 key 即可拿到最新快照
        # 注意：updatedAt 每轮都刷，事件密度 ≈ 车数 / 周期；
        # 仅关心业务字段变化时，本地对比上一次值再决定是否处理
        ...
```

### 9.3 命令行验证

观察 `updatedAt` 是否在持续推进（最直接的"采集器还活着"信号）：

```sh
# 每秒打一次，watch ms 列在增长
redis-cli HGET vfsmp:vehicle:4G2501080001 updatedAt
redis-cli HGET vfsmp:vehicle:4G2501080001 updatedAt
redis-cli HGET vfsmp:vehicle:4G2501080001 updatedAt
```

其他常用：
```sh
redis-cli SMEMBERS vfsmp:vehicles:active
redis-cli HGETALL vfsmp:vehicle:4G2501080001
redis-cli GET vfsmp:adapter:heartbeat
redis-cli GET vfsmp:schema:version
redis-cli SCARD vfsmp:vehicles:active
```

---

## 10. 异常 / 边界场景对照表

| 场景 | Redis 表现 | 订阅方应对 |
| --- | --- | --- |
| 适配器进程崩溃 | `heartbeat` 10s 内过期 → `nil`；所有车 `updatedAt` 停滞 | 整体显示"采集中断" |
| 数据源全断（EosDapi 读不到） | `heartbeat` 停止刷新；所有车 `updatedAt` 停滞 | 同上 |
| 单车采集失败（局部） | 该车 HASH 保留上轮数据；**该车 `updatedAt` 不变**（其他车正常推进） | `now - updatedAt > PollIntervalMs × 3` 判定该车陈旧 |
| 车辆从配置中删除 | `active` SREM + `vehicle:{id}` DEL | 收到 keyspace 通知后从 UI 移除 |
| 新车上线 | `active` SADD + `vehicle:{id}` HSET | 收到 keyspace 通知后从 UI 加入 |
| 网络抖动 / Redis 单次失败 | TTL 10s 给 3 次重试余量，通常不影响订阅 | 不需要特殊处理 |

---

## 11. 与上一版（v1.0）的差异

> 升级要点供已经实现 v1.0 订阅方参考；新接入方可跳过本节。

- **`updatedAt` 不再"按 diff 刷新"**：v1.0 中字段不变就不更新，导致正常运行也会出现"几秒~几十秒不动"，曾被误判为采集卡死。v1.1 改为每轮成功采集都刷（单字段 HSET，写入开销 ≪ 全量 HSET）。
- **新增"陈旧判定"路径**：v1.1 起可以直接靠 `now - updatedAt` 判断单车数据新鲜度，不再需要绕道心跳。
- **keyspace 事件密度提升**：v1.0 事件 ≈ 业务变化频率；v1.1 事件 ≈ 车数 × 轮次。仅关心业务变化的订阅方需做客户端去抖（见 §9.2 注释）。
- **HSET 原子性范围保留**：整车刷新仍是 8 字段单条 HSET；只刷时间戳是单字段 HSET，两者各自原子，订阅方读到的不会半新半旧。

---

## 12. 联系

写入方实现：`EmsToRedis` 项目 — `https://github.com/backspace-xdw/EmsToRedis`
协议主文档：参见仓库 `README.md` §10 协议对照表
告警规则源码：`Logic/AlarmRule.cs`
diff / 时间戳逻辑：`Workers/SyncWorker.cs` + `Logic/DiffTracker.cs` + `Redis/RedisWriter.cs`
