# 表格列宽优化 + 全列悬停提示(ToolTip)技术方案(编码级,2026-09-06)

> 来源:用户实测反馈(0.2.7 运行良好,API 39ms)——① 操作列"结束"被遮挡;② 要求所有
> 表格列在内容截断时悬停显示完整内容。
> **状态:仅方案,未经审核同意不改任何代码、不执行 git 操作。**
> 执行约定:实现后 `dotnet build` 编译回归 + 重打包;不自动 git 提交。

---

## 一、回填状态表

> 已回填(2026-09-06):全部实施,编译回归 0 警告 0 错误,setup.exe 0.2.7 重出
> (内嵌 UI exe 哈希一致);运行验证待用户安装复测。

| 编号 | 事项 | 实现时间 | 编译回归 | 运行验证 | 最终是否成功 |
|---|---|---|---|---|---|
| UIW-1 | 进程管理列宽重排:命令行改星号自适应 + 操作列 70 修宽(遮挡修复) | 2026-09-06 | ✅ | 待安装复测 | 待回填 |
| UIW-2 | 进程管理 5 个 TextColumn(含端口)转模板列 + ToolTip | 2026-09-06 | ✅ | 待安装复测 | 待回填 |
| UIW-3 | Top-N 5 个 TextColumn(#/平均值/峰值/单位/指标)转模板列 + ToolTip | 2026-09-06 | ✅ | 待安装复测 | 待回填 |
| UIW-4 | 告警 5 个 TextColumn(指标/当前值/阈值/级别/PID)转模板列 + ToolTip | 2026-09-06 | ✅ | 待安装复测 | 待回填 |
| UIW-5 | 重打包 setup.exe | 2026-09-06 | ✅ 53MB,哈希一致 | 待安装复测 | 待回填 |
| UIW-6 | (用户追加)趋势分析详细信息列 `*,*,*,*`→`*,*,1.2*,2*`——FirstSeen/LastSeen 时间戳 7 位小数完整显示 | 2026-09-06 | ✅ | 待安装复测 | 待回填 |
| UIW-7 | (用户追加)Top-N 进程名列 200→400;可视化条形图名称列 160→320 | 2026-09-06 | ✅ | 待安装复测 | 待回填 |
| UIW-8 | (用户澄清追加)告警页时间列 180→220——Timestamp 含 7 位小数秒(26 字符)原宽显示不全 | 2026-09-06 | ✅ | 待安装复测 | 待回填 |

**实施说明(与方案的偏差/额外发现)**:
1. **顺带修复存量 bug**:Top-N"指标"列原 TextColumn 绑定的 `Metric` 属性在
   `TopNResultDto` 上**不存在**(反射绑定静默失败,该列一直显示为空);本次转模板列后
   Avalonia 编译期绑定检查(AVLN2000)暴露,已改为正确属性 `MetricName`(转换器对
   MetricName 的值"cpu"/"memory"/"i/o read"等均有中文映射)。
2. REF 类似先例:两次编译补 `using System.Reflection`(CS1061)与本次 AVLN2000,
   均为"转模板列/动态化"时编译期检查暴露的存量隐性问题——转换本身有益。

---

## 二、现状盘点(三个 DataGrid)

### 2.1 进程管理(`ProcessManagerView.axaml`)— 遮挡现场

| 列 | 当前宽 | 类型 | ToolTip |
|---|---|---|---|
| PID | 70 | Text | ✗ |
| 进程名 | 240 | 模板 | ✓(exe 名) |
| 命令行 | 280 | 模板 | ✓ |
| 线程数 | 80 | Text | ✗ |
| 内存 (MB) | 90 | Text | ✗ |
| CPU (%) | 70 | Text | ✗ |
| 端口 | 160 | Text | **✗(端口列表长,最易截断)** |
| 操作 | 90 | 模板(按钮) | — |

固定宽合计 **1080** + DataGrid 边距 32 + 纵向滚动条 ~17 ≈ **1129px**——窗口稍窄就出
横向滚动条,操作列(最右)首先被裁掉,按钮"结束"只剩一半(用户截图实况)。

### 2.2 Top-N(`TopNView.axaml`)

`#`(45)、进程名(200,模板✓)、服务名(150,模板✓)、平均值(100,Text)、峰值(100,Text)、
单位(70,Text)、指标(100,Text,转换器)。合计 765,无遮挡问题;Text 列缺 ToolTip。

### 2.3 告警(`AlertView.axaml`)

时间(180,模板✓)、进程名(180,模板✓)、PID(70,Text)、服务名(140,模板✓)、
指标(100,Text)、当前值(90,Text)、阈值(90,Text)、级别(80,Text)。
合计 930,窗口 >1000 无遮挡;Text 列缺 ToolTip。

---

## 三、修复方案(编码级)

### 3.1 UIW-1 进程管理列宽重排(根治遮挡)

策略:**命令行列改星号(`Width="*"`)+ 收窄固定列**——星号列吸收剩余空间,
DataGrid 恒等于视口宽,**横向滚动条消失,操作列永远完整可见**;窄窗口下命令行靠
ToolTip 兜底(其已有 ToolTip)。

```xml
<DataGridTemplateColumn Header="进程名" Width="220" ...>        <!-- 240→220 -->
<DataGridTemplateColumn Header="命令行" Width="*" MinWidth="200"
                        SortMemberPath="CommandLine"> ...       <!-- 280→星号 -->
<DataGridTextColumn   Header="线程数" Width="70" ...>           <!-- 80→70 -->
<DataGridTextColumn   Header="内存 (MB)" Width="80" ...>        <!-- 90→80 -->
<DataGridTextColumn   Header="CPU (%)" Width="65" ...>          <!-- 70→65 -->
<DataGridTextColumn   Header="端口" Width="120" ...>            <!-- 160→120 -->
<DataGridTemplateColumn Header="操作" Width="70"> ...           <!-- 90→70;按钮
     Padding 6,3 保持("结束"2 字 ≈44px,70 足容) -->
```

固定宽合计 70+220+70+80+65+120+70 = **695** + 命令行星号;窗口 ≥~980px 即无横向
滚动,操作列恒可见。用户手动拖宽列超视口时滚动条自然出现(可接受)。

### 3.2 UIW-2/3/4 TextColumn → 模板列统一 ToolTip

把三个 DataGrid 中所有 `DataGridTextColumn` 改为**轻量模板列**(与既有进程名/命令行
同款模式),`TextTrimming="CharacterEllipsis"` + `ToolTip.Tip` 绑定同字段;数值列保留
原 StringFormat,ToolTip 同格式带单位更友好;模板列补 `SortMemberPath` 保持可排序。

**统一模式**(以进程管理"端口"为例,它也是截断重灾区):

```xml
<DataGridTemplateColumn Header="端口" Width="120" SortMemberPath="Ports">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate x:DataType="dto:ProcessInfoDto">
            <TextBlock Text="{Binding Ports}"
                       ToolTip.Tip="{Binding Ports}"
                       TextTrimming="CharacterEllipsis"
                       VerticalAlignment="Center" />
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

数值列示例(内存,ToolTip 带原 StringFormat):

```xml
<DataGridTemplateColumn Header="内存 (MB)" Width="80" SortMemberPath="WorkingSetMb">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate x:DataType="dto:ProcessInfoDto">
            <TextBlock Text="{Binding WorkingSetMb, StringFormat='{}{0:F0}'}"
                       ToolTip.Tip="{Binding WorkingSetMb, StringFormat='{}{0:F1} MB'}"
                       TextTrimming="CharacterEllipsis"
                       VerticalAlignment="Center" />
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

**逐列清单**:

| 文件 | 列(转换 + ToolTip 绑定) |
|---|---|
| ProcessManagerView | PID(ToolTip=Pid)、线程数、内存(ToolTip 带 MB)、CPU(%F1)、**端口**(最长内容,重点) |
| TopNView | #(Rank)、平均值(AvgValue F2)、峰值(MaxValue F2)、单位(Unit)、指标(MetricName 转换器) |
| AlertView | PID、指标(Metric 转换器)、当前值(Value F1)、阈值(Threshold F1)、级别(Severity 转换器) |

**设计取舍(如实说明)**:ToolTip 采用"每格常驻"而非"仅截断时显示"——后者需要自写
附加属性比较渲染宽度与期望宽度,复杂度高、收益低;常驻 ToolTip 在未截断时悬停显示
同值,无害且是业界常规做法(DataGrid 自带单元格默认无 tooltip)。

### 3.3 不改动项

- 仪表盘 Top CPU/内存 ItemsControl 列表、Top-N 可视化列表:上一轮已加 ToolTip ✓;
- 列宽:TopN(合计 765)/告警(合计 930)无遮挡史,保持;如后续窄窗口出现同类问题,
  可复制"星号列"模式(方案已验证)。

## 四、验证计划

1. 编译回归 0 警告 0 错误;重打包 setup.exe(0.2.7 重出);
2. 安装后:
   - 进程管理:窗口 ≥980px 时无横向滚动条,操作列"结束"完整可见可点;
   - 悬停端口/命令行/进程名/内存等列 → 显示完整内容;数值列 ToolTip 带格式;
   - 各列点击表头排序仍生效(SortMemberPath 已补);
   - Top-N/告警页悬停 Text 列显示完整内容;布局无回归。

## 五、交付物与留痕

- 本文档审核通过后实施,回填第一节;
- 建议 commit:`fix(ui): 操作列遮挡修复(星号列宽)+ 全表格列悬停完整内容提示`。
