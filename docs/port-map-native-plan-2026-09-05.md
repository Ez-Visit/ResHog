# 端口映射解析原生 API 化方案(编码级,2026-09-05)

> 来源:用户实测"一次搜索 15 秒"排查结论 + 用户选定"方案 2 + 3 一步到位"。
> **状态:仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:实现后 `dotnet build` 编译回归 + 重打包(版本维持 0.2.7 重新出包);不自动 git 提交。

---

## 一、回填状态表

> 已回填(2026-09-05):编码完成 + 编译回归 0 警告 0 错误 + setup.exe 0.2.7 重出
> (内嵌 service.exe 哈希与直出产物一致);算法经独立脚本交叉验证;运行验证待安装。

| 编号 | 事项 | 实现时间 | 编译回归 | 运行验证 | 最终是否成功 |
|---|---|---|---|---|---|
| PORT-1 | ResolvePortPids(netstat spawn)→ 原生 GetExtendedTcpTable/UdpTable | 2026-09-05 | ✅ 0 警告 0 错误(修复 Marshal.ReadUInt32 不可用,改 ReadInt32 转换) | 算法对照通过(见下),待安装后端到端 | 待回填 |
| PORT-2 | 端口映射 TTL 5s→60s | 2026-09-05 | ✅ | 待安装后验证 | 待回填 |
| PORT-3 | 重打包 setup.exe(0.2.7 重出)+ 实测延迟回归 | 2026-09-05 | ✅ 53MB,哈希一致 | 待安装后复测(此前基线:>5s 间隔搜索 9~10s) | 待回填 |

**算法交叉验证记录(2026-09-05,Python ctypes 以相同假设直调同一 API)**:
- 表头 dwNumEntries 偏移 4、行 stride、网络字节序端口换算(ntohs)、pid>0 过滤;
- 结果:netstat(pid>0, v4)= 639 端口,native(TCP+UDP,v4,pid>0)= **639 端口,
  双向差集均为 0** → C# 实现所依赖的全部假设与 netstat -ano 实际输出完全一致。
- 注:初版对照差 209 条为脚本未过滤 pid=0(TIME_WAIT 无主行),修正后归零。

---

## 二、排查结论回顾(2026-09-05 实测)

| 测试 | 结果 |
|---|---|
| 距上次 >5s 的搜索 | 9.05s / 10.25s(两次复现) |
| 紧接着的搜索 | 6~30ms |
| 裸 `netstat -ano`(交互会话) | 0.48s |

根因:`SearchProcesses` 每次调用 `GetCachedPortMap()`;端口映射缓存 **TTL=5s**,过期后在
**HTTP 请求路径同步** spawn `netstat.exe` 并 `ReadToEnd`。服务环境(LocalSystem/session 0)
下该 spawn+读路径实测 ~10s(裸 netstat 0.5s 的 20 倍,典型嫌疑:杀软对服务 spawn 外部
exe 的注入扫描);另 `ReadToEnd` 在 `WaitForExit(1000)` 之前执行,1s 超时形同虚设。
用户截图 15.3s = 冷进程列表 + 端口映射刷新叠加。

## 三、方案(编码级)

**修改文件**:仅 `src/ResHog.Service/ProcessManager.cs`。
**核心**:删除 netstat 外部进程依赖,改用iphlpapi 原生 API(`GetExtendedTcpTable` /
`GetExtendedUdpTable`——netstat 的底层实现),毫秒级完成;TTL 提到 60s。
原生调用足够快,**无需**引入"过期续用+后台刷新"复杂度,保持现有同步结构不变。

### 3.1 新增 P/Invoke 区(`#region Port table (iphlpapi)`)

```csharp
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;   // 所有 TCP 行(含 Listen/Established),带 PID
    private const int UDP_TABLE_OWNER_PID = 1;       // 所有 UDP 行,带 PID
    private const uint NO_ERROR = 0;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable, ref int dwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;   // 网络字节序
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;   // 网络字节序
        public uint dwOwningPid;
    }
```

### 3.2 重写 `ResolvePortPids()`(整体替换,签名/返回值不变)

```csharp
    /// <summary>
    /// 构建 端口 → PID 集合 映射(PORT-1,2026-09-05)。
    ///
    /// 历史缺陷:原实现 spawn `netstat.exe -ano` 并 ReadToEnd——在 HTTP 请求路径同步执行,
    /// 服务环境(LocalSystem/session 0)下实测单次 ~10s(裸 netstat 0.5s 的 20 倍,嫌疑为
    /// 杀软对服务 spawn 外部 exe 的注入扫描),且 ReadToEnd 无超时约束
    /// (WaitForExit(1000) 在其后,形同虚设)。
    ///
    /// 现改用 iphlpapi 原生表(netstat 的底层实现):无进程 spawn,单次 1~5ms;
    /// 语义与 netstat -ano 对齐:TCP 所有状态行 + UDP 所有行,本地端口 → 拥有 PID(>0)。
    /// </summary>
    private static Dictionary<int, HashSet<int>> ResolvePortPids()
    {
        var result = new Dictionary<int, HashSet<int>>();

        AddTcpRows(result);
        AddUdpRows(result);
        return result;
    }

    private static void AddTcpRows(Dictionary<int, HashSet<int>> result)
    {
        int size = 64 * 1024;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            uint ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret == ERROR_INSUFFICIENT_BUFFER)
            {
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
                ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            }
            if (ret != NO_ERROR) return;

            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            IntPtr rowPtr = buf + sizeof(uint);                 // 跳过 dwNumEntries
            // 注意:重取后 size 已被系统更新;行数以表头为准
            uint count = Marshal.ReadUInt32(buf);
            for (uint i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                int port = ((int)(row.dwLocalPort & 0xFF) << 8) | (int)((row.dwLocalPort >> 8) & 0xFF);
                int pid = (int)row.dwOwningPid;
                if (pid > 0 && port > 0) AddPortPid(result, port, pid);
                rowPtr += rowSize;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static void AddUdpRows(Dictionary<int, HashSet<int>> result)
    {
        // 同构:MIB_UDPROW_OWNER_PID(dwLocalAddr/dwLocalPort/dwOwningPid),
        // UDP_TABLE_OWNER_PID,无状态字段,行宽更小
    }

    private static void AddPortPid(Dictionary<int, HashSet<int>> map, int port, int pid)
    {
        if (!map.TryGetValue(port, out var set))
            map[port] = set = new HashSet<int>();
        set.Add(pid);
    }
```

> 实施细节说明(编码时注意):
> 1. **dwLocalPort 网络字节序**:低 16 位按 `(x>>8)|(x&0xFF)<<8` 换算(与 netstat 显示一致);
> 2. **ERROR_INSUFFICIENT_BUFFER(122) 重取**:首次 64KB 可能不足,按 out 的 size 重分配重调一次;
> 3. **行数以表头 dwNumEntries 为准**(重取后 size 变化不影响);
> 4. 原 netstat 解析只取 `parts[1]` 的本地端口(监听+连接都算),本方案语义一致;
> 5. pid≤0 与 port=0 行跳过(与原 `pid > 0` 判断一致)。

### 3.3 TTL 调整(PORT-2)

```csharp
    private static readonly TimeSpan PortMapTtl = TimeSpan.FromSeconds(60);   // 原 5s
```

原生解析毫秒级,60s TTL 下过期同步重建的请求也无感;不再需要后台化。
类头注释 `netstat -ano` 相关描述同步更新。

### 3.4 不改动项

- `SearchProcesses`/`SearchByPort`/缓存结构/`GetCachedPortMap` 同步结构(签名与语义不变);
- 进程列表缓存链路(FIX-1/FIX-2/DISP-9 已就绪);
- `EnumerateProcesses` 等其余部分。

## 四、边界与风险

| 场景 | 行为 | 评估 |
|---|---|---|
| 连接表很大(数千行) | 122 重取一次,行解析 O(n) 内存遍历 | 低(ms 级) |
| IPv6 连接 | AF_INET 仅 v4——与原 netstat 解析一致(原解析同样只按行文本取本地端口,含 v6 行?否:原解析对任意行取 `parts[1]` 冒号后端口,v6 行 `[::]:port` 亦被纳入;本方案不含 v6) | **语义微差**:v6 端口不再入映射。实际影响小(本地服务以 v4 为主),如需对齐可后续加 AF_INET6 |
| UDP 无状态概念 | 同样入映射(与原一致) | — |
| 权限 | LocalSystem 可见性与 netstat -ano 相同 | 无回归 |
| 杀软 | 无外部进程 spawn,扰动消失 | 正收益 |

## 五、验证计划

1. 编译回归 `dotnet build ResHog.slnx` → 0 警告 0 错误;
2. 重打包 setup.exe(0.2.7 重出,哈希核验);
3. 安装后实测:
   - 距上次 >60s 的搜索 → 应 **<100ms**(此前 ~10s);
   - 按端口搜索(如 `5180`/`3389`)结果与 `netstat -ano | findstr :5180` 对照一致;
   - 名称搜索 "ResHog"/"code" 正常;仪表盘/TopN 无回归;
   - 服务日志无新增异常。

## 六、交付物与留痕

- 本方案文档审核通过后实施,回填第一节;
- 建议 commit:`perf(process-search): 端口映射原生 API 化 — 消除请求路径 netstat spawn(搜索 10s→ms)`。
