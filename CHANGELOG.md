# 变更日志

本项目变更日志遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/) 格式，
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增

- 项目初始化，完成技术方案评估书（v1.3）
- 确定技术栈：.NET 10 LTS + PDH 性能计数器 + Avalonia UI
- 确定项目名：ResHog（Resource + Hog）

### 待开发

- Phase 1：核心采集引擎（进程枚举 + PDH 计数器 + SQLite 存储）
- Phase 2：分析报告引擎（Top-N 排行 + HTML 报告 + 告警）
- Phase 3：数据聚合优化（分钟/小时聚合 + 保留策略）
- Phase 4：本地 HTTP API（Kestrel）
- Phase 5：Avalonia 桌面客户端
- Phase 6：测试与部署

---

## 版本号规则

| 版本变更 | 触发条件 | 示例 |
|----------|---------|------|
| **主版本 (X.0.0)** | 不兼容的 API 变更 | 从 v1.x 升级到 v2.0.0 |
| **次版本 (1.X.0)** | 向后兼容的新功能 | v1.0.0 → v1.1.0 |
| **修订版本 (1.0.X)** | 向后兼容的 Bug 修复 | v1.0.0 → v1.0.1 |
| **预发布** | Alpha/Beta/RC 版本 | v1.0.0-alpha.1 |

## 链接

[Unreleased]: https://github.com/ResHog/ResHog/compare/HEAD
