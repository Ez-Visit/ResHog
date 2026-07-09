# 贡献指南

感谢你对 ResHog 的关注！本文档说明如何参与项目开发。

## 行为准则

参与本项目即表示你同意遵守 [行为准则](CODE_OF_CONDUCT.md)。请始终保持尊重和友善。

## 开发环境准备

### 必需工具

| 工具 | 版本要求 | 说明 |
|------|---------|------|
| .NET SDK | 10.0.100+ | [下载](https://dotnet.microsoft.com/download) |
| Git | 2.40+ | 版本控制 |
| Visual Studio 2026 / Rider / VS Code | 最新版 | IDE（任选其一） |
| Windows 10 1809+ | — | 本项目仅支持 Windows |

### 可选工具

| 工具 | 用途 |
|------|------|
| Avalonia for Visual Studio | XAML 预览与诊断 |
| dotnet-format | 代码格式化 |
| SQLite Browser | 查看数据库内容 |

## 开发流程

### 1. Fork & Clone

```bash
# Fork 仓库后 clone 你的 fork
git clone https://github.com/<你的用户名>/ResHog.git
cd ResHog

# 添加上游远程
git remote add upstream https://github.com/ResHog/ResHog.git
```

### 2. 创建分支

```bash
# 从最新的 main 创建功能分支
git checkout main
git pull upstream main
git checkout -b feature/your-feature-name
```

分支命名规范：

| 前缀 | 用途 | 示例 |
|------|------|------|
| `feature/` | 新功能 | `feature/gpu-monitoring` |
| `fix/` | Bug 修复 | `fix/counter-instance-conflict` |
| `docs/` | 文档 | `docs/deployment-guide` |
| `refactor/` | 重构 | `refactor/storage-layer` |
| `test/` | 测试 | `test/alert-engine` |

### 3. 编码 & 测试

```bash
# 还原依赖
dotnet restore

# 构建
dotnet build

# 运行全部测试
dotnet test

# 运行特定测试项目
dotnet test tests/ResHog.Tests

# 格式化代码（提交前必做）
dotnet format
```

### 4. 提交

遵循 [Conventional Commits](https://www.conventionalcommits.org/) 规范：

```
<type>(<scope>): <subject>

<body>

<footer>
```

**Type 取值**：

| Type | 说明 |
|------|------|
| `feat` | 新功能 |
| `fix` | Bug 修复 |
| `docs` | 文档变更 |
| `style` | 代码格式（不影响逻辑） |
| `refactor` | 重构（非新功能、非修复） |
| `perf` | 性能优化 |
| `test` | 测试相关 |
| `chore` | 构建/工具链变更 |
| `ci` | CI 配置 |

**示例**：
```
feat(collector): add GPU utilization counter via NVAPI

fix(storage): handle SQLite WAL checkpoint deadlock on high I/O

docs(readme): add installation instructions for Server 2022
```

### 5. 推送 & 创建 PR

```bash
git push origin feature/your-feature-name
```

然后在 GitHub 上创建 Pull Request，请填写 [PR 模板](.github/PULL_REQUEST_TEMPLATE.md) 中的所有项。

## 代码规范

### C# 代码风格

- 遵循 `.editorconfig` 中的规则（项目根目录已配置）
- 使用 `var` 声明局部变量（类型明显时）
- 使用表达式体成员简化简单方法/属性
- 异步方法后缀加 `Async`，返回 `Task` 或 `Task<T>`
- 公共 API 添加 XML 文档注释

```csharp
// 推荐
public async Task<List<ProcessSample>> CollectAsync(CancellationToken ct = default)
{
    var processes = Process.GetProcesses();
    // ...
}

// 不推荐
public List<ProcessSample> Collect()  // 同步方法，阻塞调用方
{
    Process[] processes = Process.GetProcesses();  // 显式类型，不必要
    // ...
}
```

### XAML 风格

- 属性每个一行（超过 2 个属性时）
- 使用 `CompiledBindings`（已在 csproj 中全局启用）
- ViewModel 绑定使用 `x:DataType` 指定

### 测试规范

- 测试方法命名：`MethodUnderTest_Condition_ExpectedResult`
- 使用 xUnit + FluentAssertions
- 每个 public 方法至少一个测试用例
- 集成测试标记 `[Trait("Category", "Integration")]`

## Pull Request 检查清单

提交 PR 前请确认：

- [ ] 代码通过 `dotnet build` 无错误无警告
- [ ] 所有测试通过 `dotnet test`
- [ ] 代码已格式化 `dotnet format`
- [ ] 提交信息符合 Conventional Commits 规范
- [ ] 新增功能已编写对应测试
- [ ] 公共 API 已添加 XML 文档注释
- [ ] 如有 Breaking Change，已在 PR 描述中标注
- [ ] 更新了相关文档（如适用）

## Issue 指南

### 报告 Bug

使用 [Bug 报告模板](.github/ISSUE_TEMPLATE/bug_report.yml)，请包含：

- 操作系统版本（`winver` 输出）
- ResHog 版本
- 复现步骤
- 预期行为 vs 实际行为
- 日志文件（`%PROGRAMDATA%\ResHog\logs\`）

### 提出功能建议

使用 [功能请求模板](.github/ISSUE_TEMPLATE/feature_request.yml)，请说明：

- 要解决的问题
- 期望的解决方案
- 替代方案（如有）

## 项目结构

详见 [README.md](README.md#项目结构) 中的项目结构说明。

## 发布流程

项目维护者参考：

1. 更新 `CHANGELOG.md`
2. 更新版本号（csproj 中的 `Version`）
3. 创建 tag：`git tag -a v1.0.0 -m "Release v1.0.0"`
4. 推送 tag：`git push origin v1.0.0`
5. GitHub Actions 自动构建并创建 Release

## 联系方式

- GitHub Issues：[提交 Issue](https://github.com/ResHog/ResHog/issues)
- GitHub Discussions：[参与讨论](https://github.com/ResHog/ResHog/discussions)

---

再次感谢你的贡献！🐷
