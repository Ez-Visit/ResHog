# 离线健康诊断与优化建议(Health Advisory)技术方案 v1(2026-09-06)

> 约束(用户已定):**纯离线不联网**(不接 LLM)、**只推荐不执行**(不做 kill/停服务)、
> **预警规则可配置**(用户可调阈值与条件)、基于本地历史数据 + 进程树逻辑做粗略评估与筛选。
>
> ## 执行状态(P0 已全部实施,2026-09-06 回填)
>
> - 引擎:findings 表(SchemaSql 幂等,存量库启动自建)、RuleStore(rules.json,缺省 8 条出厂规则)、
>   AdvisoryRuleEngine(R1~R8 全部实现,去重冷却/自动 resolve/排除名单),Worker 每分钟聚合后评估;
> - 进程树:ProcessManager 枚举接入 Toolhelp32 快照(全系统 pid→ppid 一次调用),ProcessInfoDto
>   增 `ParentPid`;R7 证据含"同父进程 PID x"归并信息;
> - API:GET /api/findings?status=active|history、POST /api/findings/{id}/ignore、GET|PUT /api/rules;
> - UI:新「诊断建议」Tab(当前建议卡片[严重度色条/结论/证据/建议/不再提醒] + 历史 DataGrid +
>   规则编辑器[启用/级别/阈值/窗口/冷却,保存后下一分钟生效]);
> - 编译回归 0 警告 0 错误;setup.exe 0.2.7 重出(内嵌哈希一致)。
> - 实施备注:①R7/R8 语义微调——R7 用分钟表 sample_count 做实例数(历史可用),R8 用 raw 表
>   窗口内 svchost 按 pid 找主要贡献者再经 ServiceMapper 归因;②"仅截断时提示"仍为常驻
>   ToolTip 方案;③规则 ExcludeProcesses 编辑暂未开放(预设含 ResHog.Service),二期开放。
> - 运行验证:待用户安装复测(观察 rules.json 生成、findings 触发、冷却去重、规则保存)。

---

## 一、回填状态表

| 阶段 | 内容 | 状态 |
|---|---|---|
| P0-1 | findings 表 + RuleEngine 骨架 + R1/R2/R6 | ✅ 已实现 |
| P0-2 | R3 基线 / R4 泄漏斜率 / R7 同名聚合(含 PPID)/ R8 服务归因 | ✅ 已实现 |
| P0-3 | API(findings/rules)+ rules.json 持久化 | ✅ 已实现 |
| P0-4 | UI Tab(卡片/历史/规则编辑器) | ✅ 已实现 |
| P1 | 健康评分 / 每日报告 / 规则导入导出 / ExcludeProcesses 编辑 | 未开始(二期) |

---

## 一、对标分析:能借鉴什么

### 1.1 Windows 自带工具

| 工具 | 有什么 | ResHog 可借鉴点 | 我们不做的 |
|---|---|---|---|
| 任务管理器 | 进程瞬时占用、启动影响、UWP 历史占用 | "应用历史"思想:**跨时段聚合视图** | UWP 专项 |
| 资源监视器 resmon | CPU(每进程关联服务/句柄)、内存(**每进程硬错误/分钟**、提交/工作集分解)、磁盘(**按文件 I/O**、队列长度、响应时间)、网络(每进程连接/流量) | ① 按服务归因(已有 ServiceMapper);② **磁盘队列/响应时间**做"磁盘瓶颈"规则;③ 内存"硬错误率"做"内存压力"规则 | 按文件 I/O、句柄表(需 ETW/内核计数器,重) |
| 性能监视器 perfmon | 计数器日志、数据收集器集、**基线对比** | **基线思想是灵魂**:与自身 7 天同时段均值对比的异常检测(见规则 R3) | 通用计数器框架 |
| 可靠性监视器 | 故障/变更时间线 | 发现与事件的时间关联 | 未来项 |

### 1.2 行业工具(前次调研结论的落地化)

| 工具 | 机制 | ResHog 落地 |
|---|---|---|
| Process Lasso **Watchdog** | "进程 X 满足条件 → 动作"规则引擎 | **规则引擎原型**:条件(指标/窗口/持续时间)+ 建议动作(只文本);用户可增删改 |
| System Informer 进程树 | 父子进程聚合视图 | **进程树聚合**:同 exe 多实例(如 chrome ×30)归并为一条建议,列合计占用 |
| APM 最佳实践(Datadog 等) | 异常检测:当前值 vs 滚动基线的偏离度(z-score 思想) | **自基线异常**规则:进程 CPU 超过自身 7 天同时段均值 ×N 倍且持续 M 分钟 |
| Trae 诊断历史(参照物) | 触发原因 + 快照 + 结论话术 | findings 表 + 模板话术;触发钩子复用 AlertEngine 阈值体系 |

### 1.3 结论:ResHog 的差异化优势

Trae 只有"触发瞬间快照",**我们有 7 天 × 分钟级历史**——离线规则诊断的准确度上限反而更高
(可以做基线对比、趋势外推、时段对比),这正是本方案的核心竞争力。

---

## 二、功能点清单(规则即功能)

### P0 规则集(出厂预设,全部用户可调:阈值/窗口/持续分钟/启停)

| # | 规则 | 评估逻辑(全部离线,基于 samples_minute) | 建议文案示例 |
|---|---|---|---|
| R1 持续高 CPU | 进程 CPU ≥ 阈值(默认 50%)持续 ≥N 分钟(默认 15) | 窗口聚合 AVG | `chrome 过去 15 分钟平均 CPU 85%(阈值 50%),建议检查其标签页/扩展,或暂时关闭` |
| R2 持续高内存 | 进程内存 ≥ 阈值(默认 1.5GB)持续 ≥N 分钟 | 同上 | `ZCode 占用内存 2.1GB,如不使用建议关闭以释放内存` |
| **R3 自基线异常** | 进程 CPU > 自身 7 天同时段均值 ×N 倍(默认 3×)且持续 M 分钟 | 按 (进程,小时) 分组的历史均值基线 | `wps CPU 较平时同时段升高 4 倍,可能正在执行后台任务或异常,建议关注` |
| R4 内存泄漏嫌疑 | 进程内存 6 小时线性斜率 > 阈值(默认 +10%/小时)且绝对值 >500MB | 对窗口内序列做线性回归斜率 | `某进程内存 6 小时增长 80%(疑似泄漏),建议在方便时重启该应用` |
| R5 磁盘写入热点 | 进程磁盘写 ≥ 阈值(默认 20MB/s)持续 ≥N 分钟;**增强:结合系统磁盘队列**(未来项) | 窗口聚合 | `某进程持续写入 45MB/s,可能正在下载/编译/同步,大量写入期间系统会卡顿` |
| R6 资源集中度 | 前 3 进程合计占系统 CPU ≥ 阈值(默认 70%) | 份额计算 | `系统 CPU 的 82% 集中在前 3 个进程,机器整体卡顿主要来自它们` |
| R7 同名进程聚合 | 同 exe 实例数 ≥N 且合计内存 ≥M(进程树逻辑,见 §三.3) | 按进程名分组聚合 | `chrome 共 28 个进程合计内存 3.2GB,属于正常架构;如需释放内存可关闭空闲标签页` |
| R8 服务归因 | 命中 svchost 时给到具体服务名(ServiceMapper) | pid→lpDisplayName | `服务主机: Windows Update 正持续占用 CPU,可考虑暂停更新服务(设置→Windows 更新)` |

### P1 增强(二期)

- **健康评分**:0-100,由活动 findings 的严重度加权;仪表盘展示
- **每日报告**:昨日 Top 消耗 vs 前 7 日均值,固定时段生成一条 findings
- **忽略机制**:单条建议"不再提醒此进程+此规则"(fingerprint 级抑制)
- 规则导入/导出(JSON)

### 明确不做(本轮)

联网/LLM、kill 与服务控制执行、按文件磁盘 I/O、网络每连接、UWP 专项、开机自启项检测。

---

## 三、架构设计

### 3.1 组件与数据流(全部复用现有地基)

```
AlertEngine(阈值告警,已有)──┐
samples_minute(7天历史,已有)─┼─→ RuleEngineService(新增,每分钟评估)
ServiceMapper(pid→服务,已有)─┤      ├─ 加载规则(rules.json)
进程列表缓存(已有)──────────┘      ├─ 逐规则评估(R1~R8)
                                    ├─ 去重/冷却(fingerprint)
                                    └─ 写 findings 表 + 推 UI 轮询
```

### 3.2 进程树逻辑(用户点名项)

- **实时树**:`ProcessManager` 枚举时已取主模块;新增 **PPID 采集**——Toolhelp32Snapshot
  (CreateToolhelp32Snapshot/Process32FirstW)一次快照拿全系统 parent PID,微秒级,
  存入 ProcessInfoDto(新增 `ParentPid` 字段),用于:树形聚合、识别"同一父进程的多实例"
- **历史聚合**:samples 表无 ppid(不为此改 schema)——历史 findings 用**进程名聚合**
  (R7),实测同名多实例 99% 即进程树叶子(chrome/node/svchost),语义等价;实时树仅用于
  增强"实例数/父进程"展示
- 树呈现:建议卡片的证据区显示"父进程 chrome.exe(4568)→ 28 个子进程"

### 3.3 数据模型

**新表 `findings`**(诊断建议记录,结构对标 Trae 诊断历史):

```sql
CREATE TABLE IF NOT EXISTS findings (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    rule_id     TEXT NOT NULL,          -- 'R1'..'R8'(用户自定义规则也有 id)
    severity    TEXT NOT NULL,          -- info / warning / critical
    subject     TEXT NOT NULL,          -- 进程名/进程组/服务名
    fingerprint TEXT NOT NULL,          -- rule_id+subject 哈希(去重冷却键)
    evidence    TEXT NOT NULL,          -- JSON:{avg,threshold,window,baseline,...}
    conclusion  TEXT NOT NULL,          -- 模板生成的结论
    suggestion  TEXT NOT NULL,          -- 建议文案
    first_seen  TEXT NOT NULL,
    last_seen   TEXT NOT NULL,
    hit_count   INTEGER DEFAULT 1,
    ignored     INTEGER DEFAULT 0,      -- 用户"不再提醒"
    status      TEXT DEFAULT 'active'   -- active / resolved(条件消失自动置)
);
CREATE INDEX IF NOT EXISTS idx_findings_fp ON findings(fingerprint, last_seen);
CREATE INDEX IF NOT EXISTS idx_findings_ts ON findings(last_seen);
```

**规则存储 `C:\ProgramData\ResHog\rules.json`**(由服务持有写入权;UI 经 API 修改——
规避 ProgramData ACL 问题:UI 以普通用户运行,不能直写 SYSTEM 创建的文件):

```jsonc
{
  "rules": [
    { "id": "R1", "enabled": true, "severity": "warning",
      "metric": "cpu", "op": "gte", "threshold": 50,
      "windowMinutes": 15, "cooldownHours": 6, "excludeProcesses": ["ResHog.Service"] }
  ]
}
```

每条规则公共字段:`enabled / severity / cooldownHours / excludeProcesses`;R1/R2/R5
有 `threshold+windowMinutes`;R3 有 `baselineMultiplier`;R4 有 `slopePercentPerHour`;
R6 有 `topN+sharePercent`;R7 有 `instanceCount+totalMemoryMb`。

### 3.4 API

```
GET  /api/findings?status=active        -- 建议(默认 active,支持 history)
POST /api/findings/{id}/ignore          -- 不再提醒(置 ignored)
GET  /api/rules                         -- 当前规则(服务端读 rules.json)
PUT  /api/rules                         -- 保存规则(服务端写盘,全量替换)
GET  /api/health-score                  -- 健康评分(P1)
```

### 3.5 UI(新 Tab「诊断建议」)

1. 顶部:健康评分卡(P1)+ 活动建议数
2. 中部:建议卡片列表——severity 色条 | 规则名 | 主体进程 | 证据数字(平均值/阈值/
   基线倍数)| 建议文案 | [查看趋势](跳转 Trend 页带参)| [不再提醒]
3. 底部 Tab:诊断历史(复用 DataGrid 风格)/ 规则设置(表格编辑:启停开关、阈值输入、
   冷却时长;保存 → PUT /api/rules)

### 3.6 评估调度与成本控制

- RuleEngineService 挂在 ResHogWorker 每分钟聚合之后(聚合完 samples_minute 即评估,
  数据新鲜且免额外查询);单次评估预算 <100ms(7 天窗口按需裁剪)
- 去重:fingerprint 相同且 last_seen < cooldownHours → 更新 last_seen/hit_count,不新增
- 自动 resolve:条件不再满足连续 M 分钟 → status=resolved(UI 历史可筛)

## 四、工作量评估

| 阶段 | 内容 | 估时 |
|---|---|---|
| P0-1 | findings 表 + RuleEngineService 骨架 + R1/R2/R6(窗口聚合类,最简单) | 2 天 |
| P0-2 | R3 基线异常 + R4 泄漏斜率 + R7 同名聚合 + R8 服务归因 | 2~3 天 |
| P0-3 | API(findings/rules)+ rules.json 持久化 | 1 天 |
| P0-4 | UI Tab(卡片列表 + 历史页签 + 规则编辑器) | 3~4 天 |
| P1 | 健康评分 + 每日报告 + 忽略机制 + 规则导入导出 | 2~3 天 |
| 合计 | MVP(P0 全部)≈ **8~10 人天**;含 P1 ≈ **10~13 人天** | |

## 五、验证计划要点

1. 规则评估正确性:构造样本(如删除/插入 samples_minute 制造持续高 CPU)验证触发与冷却;
2. 去重:同一 fingerprint 冷却期内不重复建卡;
3. 规则热生效:PUT /api/rules 后下一分钟按新阈值评估;
4. 性能:评估耗时日志 <100ms;
5. UI:卡片/历史/规则编辑往返一致。

## 六、留痕与提交

- 本文档审核通过后实施,分批提交(建议:引擎批/UI 批/规则批);
- 每阶段完成后回填第八节执行状态表(实施时补)。

---

## 七、规则引擎评估协议详解(AdvisoryRuleEngine.cs,2026-09-06 补)

### 7.1 触发链与总流程

```
ResHogWorker(每 3s 采样循环)
  └─ 每分钟:AggregateCatchUp()(samples_minute 补齐到上一完整分钟)
       └─ AdvisoryRuleEngine.Evaluate()          ← 挂在聚合后,数据保证最新
            ├─ AutoResolveStale:活动 findings 连续 30 分钟未命中 → status=resolved
            ├─ 逐条启用规则评估(单规则异常被捕获,不影响其余)
            │    ├─ 命中 → UpsertFinding(fingerprint 去重 + 冷却)
            │    └─ 未命中 → 无动作(不主动删卡,交给 AutoResolve)
            └─ 总耗时预算 <200ms(超 200ms 记 Warning 日志)
```

### 7.2 数据源与窗口语义

| 数据源 | 保留 | 用途 | 注意 |
|---|---|---|---|
| samples_minute | 7 天 | R1/R2/R3/R5/R6/R7 的窗口聚合(每分钟 1 行/进程) | `sample_count`=该分钟该进程**写入的样本行数**(≈实例数×每分钟采样次数,**不是实例数**);`avg_*`=按行平均≈单实例均值 |
| samples(raw) | 1 天 | R8 的 svchost 按 pid 归因;**R7 修复后将改用 `COUNT(DISTINCT pid)` 精确实例数** | 行数大(~25 万/15min),查询走 timestamp 主键前缀 |
| 实时进程列表缓存 | — | R7 的父进程归并展示(ProcessManager 缓存,带 Toolhelp32 PPID) | 失败不阻塞主流程 |

### 7.3 各规则算法

- **R1/R2/R5(持续高 CPU/内存/磁盘写)**:窗口聚合 `AVG(聚合指标) ≥ 阈值` 且高于噪声下限
  (CPU 5%/内存 256MB/磁盘写 1MB/s)。CPU/磁盘写为分钟表单实例均值;
  **内存(R2)2026-09-07 起改用私有工作集**(`working_set_private_mb`,查 raw 表)——
  用户对比任务管理器发现工作集口径虚高(含共享 DLL 页,GUI 程序 ×2~5),私有工作集
  与任务管理器"内存"列口径一致。R4 的泄漏斜率仍用工作集(共享映射页不随泄漏增长,
  增长信号本质上来自私有堆,趋势判定有效)。
- **R3(基线异常)**:候选=窗口内单实例 CPU≥10% 的前 10 名;逐个查其**近 7 天同一小时**
  (`substr(minute,12,2)=当前小时`)的 CPU 均值作基线;基线 ≥1% 才有效;命中条件
  `当前均值 ≥ 基线 × 倍数(默认 3×)`。
- **R4(泄漏嫌疑)**:取窗口(默认 6h)内每进程分钟序列,要求 ≥60 点、末值 ≥500MB;
  C# 线性回归得斜率,换算 `%/小时(相对序列均值)` ≥ 阈值(默认 10)判命中。
- **R6(资源集中度)**:每进程负载=单实例均值×实例行数;前 3 名负载合计 ÷ 全体负载 ≥70%
  且全体负载 ≥5(整机空闲不诊断)。**份额是比值,采样频率膨胀因子分子分母抵消,不受影响**。
- **R7(同名进程聚合)**:同 exe 多实例归并。两轮修正:
  ①(2026-09-06 用户发现)实例数误用 `sample_count`(被采样频率膨胀 13~20×,svchost
  显示 1562/实际 114)→ 改 raw 表按 pid 去重;②(2026-09-07 用户对比任务管理器)
  15 分钟窗口的 pid 去重把**短命进程**也计入(wps 启动器 15 分钟出现 99 个 pid、
  存活仅 9 个),且工作集口径含共享页(4484MB vs 任务管理器 ~226MB)→ 现改为:
  **存活口径**(仅最近 60 秒内仍有样本的 pid)+ **私有工作集**(`working_set_private_mb`
  按 pid 均值求和),两口径均与任务管理器直接可比;**并排除 svchost**——见 7.4。
- **R8(服务主机归因)**:raw 表窗口内 `process_name='svchost'` 按 pid 聚合,
  取 CPU 均值第一名的 pid,经 ServiceMapper 查其**本地化服务显示名**(如"Windows Update"),
  卡片主体显示"服务主机: <服务名>"。**内存证据用 private_bytes_mb**(2026-09-06 补):
  svchost 实例间共享大量系统 DLL,工作集合计会重复计入共享页;跨实例聚合按私有字节记账
  是行业惯例(resmon"提交"列)。
- **R9 svchost 真伪校验(2026-09-06 新增,行业惯例落地)**:合法 svchost 恒满足
  ①镜像路径 = System32\svchost.exe ②父进程 = services.exe;任一已知信息偏离 →
  **critical** 级"可疑 svchost (PID x)"(含路径/父进程证据,建议杀毒扫描)。
  未知信息不算违规(防误报);账户校验(token)成本高列二期。纯提醒不执行动作。
  (2026-09-07 修正:评估改用 **只读缓存**(ProcessManager.TryGetCachedProcessList,不触发
  刷新)+ **降频 30 分钟**——此前每 15s 用 SearchProcesses 触发后台刷新,
  实测把引擎评估拖到 6~34 秒(Advisory evaluation took 34109ms),已修复。)

- **规则 API 错配(2026-09-07 修复)**:GET /api/rules 此前返回裸数组
  (Results.Ok(store.Current)),客户端期待 AdvisoryRulesDto 包装 → 反序列化失败 →
  UI"规则加载失败,服务未响应"。现改为 Results.Ok(new AdvisoryRulesDto(store.Current)),
  与 DTO 语义对齐。

- **PUT /api/rules 500(2026-09-07 修复,真因)**:RuleStore 序列化/反序列化用普通
  JsonSerializerOptions——服务端已用源生成(ApiJsonContext)并禁用反射,Serialize 抛
  "Reflection-based serialization has been disabled"(curl 实测响应体原文)。
  修复:RuleStore 改用 ApiJsonContext 为 TypeInfoResolver;AdvisoryRulesDto.Rules
  由 IReadOnlyList 改 List(源生成只认具体类型);相应 ToList() 适配。
  佐证:rules.json 从未创建(Load 写默认的异常被 catch 吞掉,GET 返回内存默认)。

- **R4 误报 Memory Compression(2026-09-07 修复)**:Memory Compression 是 Windows 10/11
  核心系统进程(内存压缩,系统自动管理、用户无法关闭,内存随负载波动)——R4 曾把它当
  "内存增长(泄漏嫌疑)"误报(6 小时 +11%/小时)。修复:新增 SystemProcesses 排除清单
  (memory compression/system/idle/registry/smss/csrss/wininit/winlogon/services/lsass/
  svchost/dwm/explorer/...),全局 Excluded() 统一应用,各规则(含 R1/R2/R4/R5/R7)
  对系统基础设施不再发"关闭/重启"类建议;svchost 仍由 R8/R9 专属。

### 7.4 R7 与 R8 的分工(为什么 R7 要排除 svchost)

svchost.exe 是 Windows 的**服务宿主**:系统把几十个服务分装进上百个 svchost 实例,
多实例是**操作系统设计使然**,不是用户可"关闭空闲窗口"的对象:

- 用户能理解并行动的建议是"**哪个服务**在吃资源"→ 这是 **R8** 的职责(给出具体服务名);
- R7 的模板建议("关闭空闲的窗口/标签页")只适用于**有窗口的应用**(浏览器/编辑器),
  对 svchost 输出这句话既无效又制造恐慌(1562 个实例 ×15.4GB 的表述会让人以为中了病毒)。

分工定界:**R7 管"带窗口的应用"的多实例归并;R8 管"svchost/系统服务"的归因**。
因此 R7 评估时跳过 `svchost`(由 R8 专属处理);若 svchost 内存本身偏高,
未来可加 R9"服务主机内存聚合"补位(当前 R2 的单实例语义覆盖不了,如实记录为已知空档)。

### 7.5 finding 生命周期

```
新建(active, hit_count=1)
  → 冷却期内再命中:仅更新 last_seen/hit_count/evidence(不新增卡片)
  → 超过冷却时长再命中:生成新卡片(重新提醒)
  → 连续 30 分钟未命中:自动 status=resolved(历史可查)
  → 用户点"不再提醒":ignored=1(永久抑制该 fingerprint,历史保留)
```

fingerprint = MD5(ruleId | subject)。主体变化(如 svchost 主要贡献 pid 变了导致
服务名变化)会生成新卡片——符合"换了东西在吃资源"的直觉。
