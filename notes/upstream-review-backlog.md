# Unigram 原仓库审查问题清单

## 当前决策

- 登记日期：2026-10-04。
- **仅记录，暂不进行修复。** 下列未解决项全部处于暂缓状态，不是本轮实施任务。
- 只有用户后续明确授权，才启动对应修复；不得因 P1 等级自行修改源码、生成修复补丁、构建、部署或迁移数据。
- 范围：Unigram 原仓库底座，不重复审查 HotReactions。
- 上游基线：`b6eeb455251aa34cda8ba2256679cecf1fec4a03`；分析时 fork HEAD：`b7c030a6aeb4a7d2cf5324e3c49fea7b8b414c26`。
- 共 14 项未解决：8 项 P1、6 项 P2；另归档 1 项 fork 已处理的问题。
- 完整证据、触发条件、修复方向与验证方法：[完整分析报告](unigram-upstream-analysis.md)。以下等级以父会话最终报告为准。

## P1 · 暂缓修复

- [ ] **R01 · mini app 安全存储账号隔离失效**：配置反序列化丢失 StorageId，不同账号同 bot 可生成同一文件名。位置：`Telegram/Common/WebAppStorage.cs`。证据：隔离配置往返确认根因，未执行真实跨账号读取。
- [ ] **R02 · 敏感值进入诊断日志和报告**：安全存储值、剪贴板正文进入日志尾部及崩溃报告链路；调低日志级别不能避免。位置：`Telegram/Views/Host/WebAppWindow.xaml.cs`、`Telegram/Logger.cs`、`Telegram/Common/WatchDog.cs`。证据：静态数据流，未验证实际上传。
- [ ] **R03 · mini app 桥接未绑定可信来源**：跨来源导航后的页面可继承原 bot/session 的存储等桥接权限。位置：`Telegram/Controls/WebViewer.cs`、`Telegram/Views/Host/WebAppWindow.xaml.cs`。证据：静态可达边界缺陷，未执行端到端网页利用。
- [ ] **R04 · 账号销毁期间 ID/目录所有权竞争**：移除注册表后 ID 可复用，旧删除任务与新会话目录发生冲突。位置：`Telegram/Services/LifetimeService.cs`、`Telegram/Services/ClientService.cs`。证据：静态交错，实际删除结果未实机验证。
- [ ] **R05 · 连续退出留下失效活动账号**：PreviousItem 未校验注册表身份，连续退出可使 ActiveItem 指向已移除对象。位置：`Telegram/Services/LifetimeService.cs`、`Telegram/Views/Host/RootWindow.xaml.cs`。证据：静态状态演算。
- [ ] **R06 · EventAggregator 并发丢订阅**：最后退订的 handler 回收可删除刚成功加入的新订阅。位置：`Telegram/Services/EventAggregator.cs`。证据：上游原始代码在独立 .NET 10 控制台初测及三次重测均复现；未运行 UWP。
- [ ] **R07 · UpdateManager 并发丢文件通知订阅**：注册后的身份复查未封闭旧退订删除路由的窗口。位置：`Telegram/Common/UpdateManager.cs`。证据：静态交错。
- [ ] **R08 · 原生媒体 Play/Close 资源竞争**：关闭后的 Play 可发布未被清理快照捕获的 ABI 引用，m_stream 还存在数据竞争。位置：`Telegram.Native/Media/AsyncMediaPlayer.cpp(.h)`。证据：静态交错；未执行原生引用计数测试。

## P2 · 暂缓修复

- [ ] **R09 · 旧历史切片覆盖删除更新**：已删除消息可被在途旧切片重新显示。位置：`Telegram/ViewModels/DialogViewModel.cs`、`Telegram/ViewModels/DialogViewModel.Handle.cs`、`Telegram/ViewModels/Dialogs/MessageCollection.cs`。证据：静态显示合并时序；不代表服务端恢复。
- [ ] **R10 · 退出账号误关其他账号副窗口**：最终 Destroy 没有按所属 session 过滤非主窗口。位置：`Telegram/Services/LifetimeService.cs`。证据：静态分支。
- [ ] **R11 · Gallery 旧缩略图覆盖新图**：槽位复用未解除旧 token，回调未核对当前缩略图身份。位置：`Telegram/Controls/Gallery/GalleryContent.xaml.cs`。证据：静态回调身份缺陷。
- [ ] **R12 · 直接卸载漏释放保屏请求**：Unload 未配平播放时申请的 DisplayRequest。位置：`Telegram/Controls/Gallery/GalleryTransportControls.xaml.cs`。证据：静态资源配对缺陷；持续时间依系统和对象生命周期而定。
- [ ] **R13 · 远程文件 EOF 差一字节**：offset >= Size - 1 提前排除了最后一个合法字节。位置：`Telegram/Streams/RemoteFileSource.cs`。证据：静态边界。
- [ ] **R14 · 默认双架构构建参数无效**：Build.ps1 把组合值同时传给 Platform 与 AppxBundlePlatforms。位置：`Build.ps1`。证据：VS MSBuild 配置验证返回 MSB4126；单架构通过，未打包。

## 已处理的上游历史项

- [x] **R15 · TDLib C ABI 契约不一致**：上游 C# 扩展签名与官方子模块不匹配。fork 已通过 TDLib 扩展 ABI 提交及主仓库 b7c030a6a 固定对应子模块。**这是此前已处理的事实，本轮没有实施修复。** 后续是否补离线 smoke test 也需另行授权。

## 证据边界与后续规则

- 本轮没有 UWP 实机回归、真实账号/凭据访问、崩溃报告发送或完整发行构建。
- 静态确认与隔离根因验证不能写成已发生生产事故；未审范围不能视为已排除风险。
- 后续恢复处理时，先核对当前源码、上游版本及问题是否仍存在，再执行用户授权范围；不能直接按旧行号修改。
- 原报告中的修复批次只是建议，不构成实施授权。
