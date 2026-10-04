# Unigram 原仓库架构与关键路径分析

> **当前决策（2026-10-04）：仅登记，暂不修复。** 所有未解决项均暂缓；未经用户后续明确授权，不启动修复、构建、部署或数据迁移。跟踪入口：[原仓库问题清单](upstream-review-backlog.md)。

> 分析日期：2026-10-04。范围：原仓库底座，不重复审查新增 HotReactions 功能。
> 方式：固定上游快照的静态分析、三个独立只读审查方向、父会话逐项源码复核，以及两个隔离控制台测试和一次 MSBuild 配置验证。
> 本报告不是全仓逐行审计，也不是 UWP 实机安全认证；“完整”指覆盖约定的分析维度与主要关键路径。

## 1. 执行摘要

**底座值得继续使用和维护，不建议重写或大规模清理上游代码；但成熟上游并不等于生命周期和安全边界已经正确。**

本轮保留 **14 项仍影响当前 fork 的问题：8 项 P1、6 项 P2**。另外确认 **1 项上游原生接口契约问题，fork 已处理**。未发现证据足以定为 P0 的问题，这不等于证明不存在 P0。

最优先的修复顺序：

1. mini app 敏感日志脱敏、网页桥接来源隔离、安全存储标识恢复。
2. 账号销毁期间的 ID/目录所有权，以及失效 PreviousItem。
3. EventAggregator 和 UpdateManager 的订阅回收竞态。
4. 原生播放器 Play/Close 资源交接。
5. 消息历史与删除更新的一致性、其余媒体和构建边界问题。

**证据必须分开看：**

- 已执行：原始 EventAggregator 在独立 .NET 10 控制台中丢失新订阅，初测及三次重测均出现；安全存储原始配置声明及 JSON context 往返后标识为空；原始构建脚本默认平台参数的 MSBuild 配置验证失败。
- 静态确认：其余问题基于调用链、具体分支或可达并发交错，不声称已经在 Unigram UI 中复现。
- 尚未执行：真实 UWP 回归、双账号实际登录、崩溃报告实际发送、攻击网页端到端利用、原生引用计数测试、完整 Release/MSIX 构建。

本轮未修改 Unigram 源码，未安装或启动应用，未访问真实账号、日志、凭据或聊天内容，未向 Telegram 发请求。只创建仓库外报告及临时快照/测试产物。

## 2. 基线与归属

| 项目 | 已核验信息 |
|---|---|
| 实际仓库 | `G:\Dev\new telegram\Unigram` |
| fork 分支 | `feature/hot-reactions` |
| fork HEAD | `b7c030a6aeb4a7d2cf5324e3c49fea7b8b414c26` |
| 上游基线/merge-base | `b6eeb455251aa34cda8ba2256679cecf1fec4a03` |
| 上游提交日期 | 2026-09-24 |
| 远端 HEAD 核验 | 分析开始时 `git ls-remote upstream HEAD` 返回同一 `b6eeb4552`；这只表示该时点没有新的上游 HEAD |
| 固定快照 | `C:\Users\MapleFu\AppData\Local\Temp\unigram-upstream-b6eeb4552-LpdMp0`，由 `git archive` 提取 |

工作区有另一会话的排行榜相关未提交改动。分析首先读取固定上游，再核对问题对应的当前 fork 源码，防止混淆归属。末次检查 HEAD 未变化；已核对的 EventAggregator、UpdateManager、WebAppStorage、WebAppWindow、WebViewer、WatchDog、媒体核心等问题文件与上游相同。LifetimeService 只有新增排行榜关闭等待，未改变本报告指出的账号替补、ID 分配和窗口选择逻辑。

**下文源码行号默认相对于上游快照，而非当前 fork。** fork 已修改的文件可能有行号偏移。为便于阅读，`Services/`、`Views/`、`ViewModels/`、`Controls/`、`Common/`、`Navigation/`、`Streams/` 等 C# 路径有时省略仓库内的 `Telegram/` 前缀；`Telegram.Native/`、`Libraries/` 和根目录构建文件不省略。

## 3. 架构地图

```text
App / BootStrapper
  ├─ 全局 LifetimeService
  │    ├─ 账号选择、创建、销毁
  │    └─ SessionImpl（每个账号一份）
  │         ├─ SettingsService
  │         ├─ EventAggregator
  │         ├─ ClientService / 各缓存子服务
  │         ├─ 通知、网络、联系人、通话、生成等服务
  │         └─ 按需 Storage / View / CloudUpdate 等服务
  ├─ WindowContext / NavigationService
  │    └─ RootWindow → MainPage / ChatPage → ChatView
  │          └─ DialogViewModel → MessageCollection
  ├─ TDLib 边界
  │    └─ Td.Client → tdjson.dll → Telegram 网络及账号数据库
  ├─ 媒体边界
  │    └─ Gallery → VideoPlayer → RemoteFileSource / UpdateManager
  │          └─ Telegram.Native → LibVLC / FFmpeg
  ├─ 通话边界
  │    └─ Telegram.Native.Calls → tgcalls / WebRTC
  ├─ 网页边界
  │    └─ WebViewer → mini app / 网页 / 支付等宿主
  └─ 桌面桥接
       └─ Telegram.Stub / app service / 托盘与 full-trust 辅助
```

### 3.1 账号级隔离是底座的重要设计

`Telegram/Services/Session.Registrations.cs:17–75` 明确区分 Globals、Singletons、Lazy、Instances；EventAggregator、ClientService 和 Settings 是账号级服务，并不是所有账号共用一套聊天事件与缓存。SessionResolverGenerator 根据注册生成构造和 Resolve，不能把快照中没有生成输出当成缺文件。

共享服务仍有必要：账号管理、语言、密码锁、代理和下载目录等。但共享状态必须有清晰隔离契约，mini app 的应用级存储尤其不能只靠“已经加密”替代账号命名空间。

### 3.2 消息更新顺序

```text
一个 TDLib 接收线程
  → 按 native client_id 找到账号
  → ClientService.OnResult 更新缓存
  → 账号 EventAggregator.Publish 同步调用订阅者
  → DialogViewModel 根据聊天/主题筛选
  → Dispatcher 排入 UI 工作
  → MessageCollection / 消息气泡更新
```

证据：`Telegram/Td/Client.cs:98–256`，`Services/ClientService.cs:3443–3645,4392–4424`，`Services/EventAggregator.cs:123–179`。

关键约束：

- EventAggregator 本身不切 UI 线程。
- 缓存更新先于发布，但对象可能继续变化；事件不是不可变快照。
- UI 派发和后台历史构造引入第二层时序，TDLib 原始顺序不能自动保证最终 UI 的合并顺序。
- 退订不能撤销已经排入 Dispatcher 的工作。

### 3.3 导航退出不是一个 Dispose 方法

导航离开通过 `NavigatedFrom/Unsubscribe/OnNavigatedFrom` 停止计时器和 pending 消息、发送 CloseChat、保存草稿与位置，再由 ChatView 清除 Delegate、集合事件和控件引用。`DialogViewModel.Dispose` 单独看并不是完整关闭入口。

证据：`Navigation/ViewModelBase.cs:39–82`，`Navigation/Services/NavigationService.cs:391–406,454–462`，`ViewModels/DialogViewModel.cs:2739–2813`，`Views/ChatView.xaml.cs:450–497`。

### 3.4 数据所有权

| 数据 | 主要所有者 | 分析结论 |
|---|---|---|
| Telegram 授权、消息与文件数据库 | TDLib，账号数字目录 | 不应擅自替换协议/数据库实现；销毁目录必须保留账号 ID 的独占性 |
| 账号设置 | SettingsService / SettingsStore 子容器 | 清理已有子容器与内存缓存处理 |
| 全局设置、代理、下载授权 | 全局服务；代理使用 `local.db` | 与账号数据不同，不能笼统要求退出账号后删除所有内容 |
| mini app 普通存储 | 应用级 `apps_storage`，账号与 bot 文件名 | 来源与账号边界均需要验证 |
| mini app 安全存储 | 配置映射、AES-GCM 文件、Windows Credential Locker | 密码学选型合理，但标识往返缺陷破坏了隔离 |
| 崩溃诊断 | Logger 尾部 → WatchDog 报告与队列 | 存在敏感值绕过原安全存储进入明文报告的路径 |

## 4. 问题总表

P1 表示建议优先修复的安全、数据或关键可靠性问题；P2 表示应排入修复计划的展示、资源或工具问题。不是按代码风格打分。

| 编号 | 等级 | 问题 | 证据状态 | 当前 fork |
|---|---|---|---|---|
| R01 | P1 | mini app 安全存储标识反序列化丢失，账号文件名合并 | 隔离验证根因＋静态数据链 | 仍存在 |
| R02 | P1 | 安全存储值/剪贴板正文进入诊断日志与报告 | 静态数据流确认 | 仍存在 |
| R03 | P1 | 跨来源网页继承原 mini app 桥接身份 | 静态可达边界缺陷 | 仍存在 |
| R04 | P1 | 销毁中的账号 ID 可复用，旧删除任务与新目录冲突 | 静态交错确认 | 仍存在 |
| R05 | P1 | 连续退出账号后 ActiveItem 可指向已移除账号 | 静态状态演算确认 | 仍存在 |
| R06 | P1 | EventAggregator 回收 handler 时丢失并发新订阅 | 原始代码隔离复现 | 仍存在 |
| R07 | P1 | UpdateManager 回收路由时丢失并发文件订阅 | 静态交错确认 | 仍存在 |
| R08 | P1 | 原生播放器 Play/Close 竞争，额外 ABI 引用可能漏释放 | 静态交错及数据竞争确认 | 仍存在 |
| R09 | P2 | 旧历史切片覆盖删除更新，使消息重新显示 | 静态合并时序确认 | 仍存在 |
| R10 | P2 | 退出一个账号会关闭其他账号副窗口 | 静态分支确认 | 仍存在 |
| R11 | P2 | Gallery 复用后旧缩略图通知覆盖新图 | 静态回调身份缺陷 | 仍存在 |
| R12 | P2 | 播放控制条直接卸载漏释放保屏请求 | 静态资源配对缺陷 | 仍存在 |
| R13 | P2 | RemoteFileSource 在最后一个合法字节位置提前报 EOF | 静态边界确认 | 仍存在 |
| R14 | P2 | Build.ps1 默认组合 Platform 无效 | VS MSBuild 配置验证复现 | 仍存在 |
| R15 | P1 | 上游 C# 扩展 TDLib ABI 与固定子模块签名不一致 | 固定提交间静态契约确认 | fork 已处理 |

父会话将消息删除复显 R09 从子报告建议的 P1 调整为 P2：现有证据证明显示状态回退，没有证明服务端/数据库恢复或新的跨账号泄露。其余等级也不等同于宣称已经发生生产事故。

## 5. 问题详情与最小修复方向

### R01：安全存储标识丢失

- 位置：`Telegram/Common/WebAppStorage.cs:60–107,493–509,560–563`。
- 根因：配置字典键保存 StorageId，但对象的 StorageId 标记为 JsonIgnore。读配置后直接返回字典，没有从键回填对象标识。
- 触发：同一账号重开 mini app；两个已有配置的账号使用同一 bot。
- 后果：标识为空时安全文件名退化为 `_{BotId}_s`。两个账号可指向同一文件；应用级加密密钥不会补回命名空间隔离。初次正常文件也可能在重开后不可见。
- 修复：从字典键回填并校验标识；文件访问拒绝空标识；为已有退化文件制定明确迁移/恢复策略，不能自动归给任意账号。
- 验证：配置往返、A/B 文件名差异、首次保存后重开、退化文件安全处置。
- 本轮隔离测试确认两个虚构账号的 StorageId 都为 null，文件名都为 `_777_s`；没有执行真实凭据保管库和跨账号文件读取。

### R02：敏感值进入崩溃报告

- 位置：`Views/Host/WebAppWindow.xaml.cs:514–516,811–818,1051–1067,2108–2112`；`Logger.cs:75–82,123–150`；`Common/WatchDog.cs:326–342,442–465,718–719`；`Common/ExceptionSerializer.cs:62–81`。
- 根因：网页双向事件完整记录载荷，包括安全存储写入值、读取结果和剪贴板正文。Logger 先放入 200 条内存尾部，再判断日志级别；报告包含该尾部。
- 后果：专用存储已加密的值仍可进入另一个明文诊断文件；非 DEBUG 且报告发送配置可用时，存在向诊断服务发送正文的路径。
- 修复：桥接日志按允许清单仅记录事件名、状态和必要长度；脱敏必须在 Logger 接收之前。评估既有排队报告处理，并使隐私说明与实际诊断行为一致。
- 验证：用虚构敏感标记测试写入、读取、剪贴板回复，断言 Logger.Dump 和报告均无标记。发送端用替身，不能向真实服务提交测试数据。
- 限制：没有检查真实日志，没有验证实际上传成功；不能据此断言现有用户数据已经泄露。

### R03：网页桥接没有绑定可信来源

- 位置：`Views/Host/WebAppWindow.xaml.cs:80–105,473–495,651–691,781–818,1051–1067`；`Controls/WebViewer.cs:453–462,513–547`。
- 根因：窗口创建后固定 bot/session；导航仅特殊拦截 t.me 与非 HTTP(S)，其他 HTTP(S) 页面仍在原宿主打开。新文档持续注入代理，WebMessageReceived 不检查 Source。
- 触发：mini app 顶层导航/重定向到另一个可执行脚本的来源。
- 后果：目标页面可使用原 bot 的存储桥接；满足附件菜单条件时还有剪贴板请求能力。不是任意网页直接取得完整 Telegram 账号权限，手机号分享等仍有单独确认。
- 修复：明确可信 origin 集合，按协议/主机/端口校验；外部来源改为外部打开或撤销桥接。接收检查 Source，异步回复绑定来源及导航代次；EdgeHTML 回退提供等效限制。
- 验证：两个本地来源 A/B、虚构 bot/session、无 Telegram 网络；验证 B 的请求和 A 迟到回复都被阻断。

### R04：账号 ID/目录所有权竞争

- 位置：`Services/LifetimeService.cs:229,264–316`；`Services/ClientService.cs:528–531`；`Navigation/WindowContext.cs:1345–1357`。
- 根因：新 ID 由存活会话 Max(Id)+1 分配。Destroy 先移除注册表项，再 await 窗口操作，之后异步按旧 ID 删除目录。
- 触发：最大 ID 账号销毁尚未完成时创建新账号。
- 后果：新账号可复用旧 ID，旧删除任务的目标与新数据库目录一致。具体是删除、部分删除、锁异常还是初始化失败，未实机验证。
- 修复：销毁完成前保留 ID 占用，或使用不会在清理期间复用的线程安全分配规则；路径删除必须有所有权代次。
- 验证：仅使用临时目录，暂停旧销毁、创建新会话，验证 ID/路径不重合且新标记文件保留。

### R05：失效 PreviousItem 破坏账号替补

- 位置：`Services/LifetimeService.cs:195–218,258–264`；`Views/Host/RootWindow.xaml.cs:177–185`。
- 触发：A/B 两账号，B 活动、A 为 previous；先退出 B，再退出切回的 A。
- 根因：第一次切回 A 又把 B 存为 previous，随后 B 被移除；第二次 Destroy 仍优先选择 B。ActiveItem setter 拒绝无效 B，但仍继续移除 A。
- 后果：ActiveItem 指向已移除的 A，且失效 previous 阻止新登录会话兜底创建。
- 修复：选择 previous 时验证注册表身份；删除时清除指向该对象的 previous；仅使用已成功切换的替补驱动窗口。
- 验证：伪会话顺序退出，断言每步活动项均在注册表中，退出最后账号后产生有效登录会话。

### R06：EventAggregator 新订阅被回收

- 位置：`Services/EventAggregator.cs:92–118,130–132,164–214`。
- 根因：获取 handler/加入订阅，以及判空/外层 TryRemove 不是一个原子生命周期操作。
- 交错：A 取消最后订阅并判空 → B 取得旧 handler 并成功注册 → A 按 key 删除路由 → 后续发布无法找到 B。
- 后果：不是只漏一次更新，而是该订阅持续失联直到重新注册。
- 修复：使用统一的注册/退役同步协议；仅按 handler 实例条件删除仍不解决同一实例刚加入订阅。若暂不回收空 handler，应评估按聊天 key 的增长。
- 验证：本轮直接链接原始 EventAggregator.cs，两个工作线程并发订退阅，操作完成后发布；新订阅应恰好收到一次，但实测为零。初测及三次独立重测均出现。运行时为 .NET 10.0.9，不是 UWP/.NET Native。

### R07：UpdateManager 文件订阅被回收

- 位置：`Common/UpdateManager.cs:82–129,482–535`。
- 根因：最后退订判空后，外层按 token 删除；新 Subscribe 虽有字典身份复查，但复查成功之后仍可被旧退订删除。
- 后果：下载进度/完成更新丢失；远程源可超时或等到取消。同 token 非零还会令重新订阅提前返回。
- 修复：注册、判空和路由退役共享协议，不在锁内执行用户回调；不是简单给字典换个线程安全类型。
- 验证：屏障固定最后退订与新注册交错，Subscribe 返回后发布，断言新读取源收到通知。本轮未运行此专项测试。

### R08：原生播放器 Play/Close 交接缺口

- 位置：`Telegram.Native/Media/AsyncMediaPlayer.h:329–393,519–550`；`.cpp:333–347,501–553`。
- 根因：工作线程在 close_lock 内检查 closed，却在锁外执行 Play；Close 捕获并交换当时的 streamAbi，清理线程 join 后只释放该快照。
- 交错：工作已通过检查 → Close 捕获空指针 → Play 发布新的 detach_abi 引用 → cleanup 只释放旧空指针。
- 后果：额外 ABI 引用漏释放；普通 m_stream 在 Play/Close 间无共同同步区间，还存在原生数据竞争。未声称已经观察到崩溃。
- 修复：媒体源交换与禁止关闭后发布使用短同步区间；保证工作线程结束后的最后引用也被回收。避免在 UI 长锁里执行 libvlc 阻塞调用或 join。
- 验证：原生测试屏障＋假媒体源引用计数；必须检查所有权，而不只是看 UI 关闭成功。

### R09：删除更新被旧历史切片覆盖

- 位置：`ViewModels/DialogViewModel.cs:1316–1380,1581–1728`；`DialogViewModel.Handle.cs:851–958`；`ViewModels/Dialogs/MessageCollection.cs:439–454`。
- 触发：后台历史结果包含 M，构造尚未结束；UI 先执行删除 M，再接收旧切片。
- 根因：删除仅修改当前 Items，没有为在途加载登记 tombstone/代次；ReplaceSlice 又加入旧 source 全部内容。
- 后果：已删除消息重新显示，属于显示一致性回退，不是服务端恢复。
- 修复：提交切片前合并加载期间的删除，或统一有序合并结果；仅检查 chat ID 不够。
- 验证：伪历史结果暂停/删除/释放，覆盖替换、前插、追加和相册子消息。

### R10：销毁账号误关其他账号副窗口

- 位置：`Services/LifetimeService.cs:268–306`；对照 `Services/Session.cs:242–254`。
- 根因：早期 LoggingOut 有所属账号检查，最终 Destroy 却遍历全部窗口，所有非主窗口都 Consolidate。
- 后果：退出 A 也打断 B 的独立聊天窗口；没有证据说明 B 被退出或串数据。
- 修复/验证：按所属 session 关闭；伪窗口测试确保 B 副窗口不收到关闭请求。

### R11：Gallery 缩略图的旧身份残留

- 位置：`Controls/Gallery/GalleryContent.xaml.cs:156–217,322–346`。
- 根因：UpdateItem 没有先解除旧 thumbnail token。新媒体已完成缩略图时不替换 token；旧完成回调使用当前 _item，却不核对缩略图 ID。
- 后果：A 未完成 → 槽位变为 B → A 完成，可以把 A 路径当成 B 的最新模糊图请求，产生错图/回闪。
- 修复/验证：换 item 先退订和清理，回调核对媒体及缩略图身份；固定上述完成顺序测试。

### R12：直接卸载遗漏保屏释放

- 位置：`GalleryTransportControls.xaml.cs:313–329,396–406,694–699,759–763`；`GalleryCompactOverlay.xaml.cs:126–134`。
- 根因：播放申请 DisplayRequest，Stop 释放；直接 Unload 只设置 unloaded/Attach(null)，解除事件后无法再靠停止通知配平。
- 后果：控制条仍存活期间保屏申请可能残留；持续时间取决于对象及系统生命周期，不是断言永久不休眠。
- 修复/验证：Unload 做幂等释放；替身验证播放、Unload、重复 Unload 只激活和释放各一次。

### R13：EOF 差一字节

- 位置：`Streams/RemoteFileSource.cs:108,232–244,275`。
- 根因：三处用 offset >= Size - 1 判终点；offset=Size-1 时尚有一个合法字节。
- 后果：尾字节无法读出；对媒体解码的具体影响依容器和读取边界而异。
- 修复/验证：统一为 offset >= Size，并覆盖空文件、单字节文件、N-1/N 边界及同步/异步读取。

### R14：默认双架构构建参数无效

- 位置：`Build.ps1:1–7`；`Telegram.slnx:2–5`。
- 根因：arch 默认 x64|arm64，同时传给 Platform 与 AppxBundlePlatforms。后者能表示多架构，不意味着前者能表示组合解决方案平台。
- 实测：VS 专有 MSBuild 执行 ValidateSolutionConfiguration，Release/x64 退出 0；默认组合返回 MSB4126，退出 1。
- 修复：Platform 选择有效单架构，AppxBundlePlatforms 单独保留列表，或参考 Build.Modern.ps1 的解析规则；不直接运行旧脚本测试，因为它先修改 manifest。
- 影响：默认自动化打包失败，不影响现有手册中的显式单架构命令。

### R15：上游 TDLib ABI 不匹配，fork 已处理

- 上游位置：`Telegram/Td/Client.cs:61–69,89–97`；`.gitmodules`；`Libraries/tdjson/build.ps1:121–176`。
- 固定 TDLib：上游子模块为 `bc9c263e2bfee06aaab41e82db51a103376030bc`，官方头文件 `td/telegram/td_json_client.h` 为两参数 td_send 和单参数 td_receive。
- C# 却声明三参数 td_send，以及带 client/request 输出参数的 td_receive；构建脚本没有应用相应 ABI 补丁。
- 后果：从固定上游源码新构建官方 DLL，与 C# 的接口契约不一致，有启动/请求调用失败风险。不是断言上游发布包一定使用了这个未补丁二进制。
- fork 状态：已改为 `b276fa3d6b073b8f5a53b02c4972732d1b9e0e1a` 的扩展 ABI TDLib 提交，主仓库提交 b7c030a6a 固定该 fork；另有 `Libraries/tdjson/td_abi.patch`。
- 建议：保留源码/Schema/DLL 同源构建与无账号离线 ABI smoke test，不把此项再次列为当前待修 bug。

## 6. 依赖、安全与分发

### 6.1 已有保障

- `vcpkg.json` 固定 builtin-baseline `c3867e714dd3a51c272826eea77267876517ed99`。
- `Directory.Build.props` 禁用机器级 vcpkg 集成，按 triplet 分离安装根，避免 ARM64/x64 互相清空。
- LibVLC 3.0.23、WebRTC 2026-09-11 的 overlay 下载使用按架构 SHA512 校验；FFmpeg overlay 为 7.1.2 并固定源码哈希。
- TDLib 与应用使用同一 manifest 的 OpenSSL/zlib，runtime DLL 和 VLC plugins 的复制路径已有显式处理。
- NuGet 来源限定为 nuget.org 与仓库 Libraries，主应用直接引用使用明确版本。
- Win2D 的替代 projection 有重生成说明，不能单因存在预编译 DLL 就判恶意或不可信。

证据：`Directory.Build.props/targets`、`vcpkg.json`、`Libraries/vcpkg-ports/{ffmpeg,libvlc,webrtc}`、`Libraries/Libraries.md`、`Documentation/Build-instructions.md`、`Telegram/Telegram.csproj:4693–4733`。

### 6.2 本轮实际漏洞源核对

使用 NuGet 官方漏洞索引：

- https://api.nuget.org/v3/vulnerabilities/index.json
- https://api.nuget.org/v3-vulnerabilities/2026.09.26.05.43.06/vulnerability.base.json
- https://api.nuget.org/v3-vulnerabilities/2026.09.26.05.43.06/2026.10.04.05.44.02/vulnerability.update.json

在所检查的主项目直接包和两个 native NuGet 包中，该源仅返回 UWP 与 System.Text.Json 的相关旧版本范围；当前 UWP 6.2.14、System.Text.Json 10.0.10 不落入这些范围。其余所查包名未命中，当时 update 为空。

**这只是所列直接依赖在该时点、该官方源的核对，不是完整漏洞扫描。** 没有完成传递依赖、操作系统组件、WebView2 实际 runtime、预编译媒体包内部第三方组件、native 子模块及回移补丁的漏洞映射。不能据此声称整个应用“无已知漏洞”。

### 6.3 维护风险，不计为本轮确认漏洞

- 原生依赖不止 vcpkg：libwebp 等还通过子模块源码进入 Telegram.Native；不能只扫描 NuGet/vcpkg 表。
- WebRTC 说明指向 M84 衍生路线，日期版号不能代表 Chromium 主版本或安全补丁覆盖；需要 fork 补丁与上游 advisory 映射，而不是凭“旧”判定存在具体 CVE。
- 预编译包哈希证明下载内容固定，不证明源码与二进制完全可复现。
- 主包直接版本明确，但本轮没有取得完整锁定的传递依赖/SBOM。BenchmarkDotNet 还使用浮动版本，但它属于隔离性能工具，不是据此认定应用运行时漏洞。
- 临时开发签名证书与生产分发信任必须分开；不应将仓库临时 PFX 用作独立产品的生产信任根。
- fork 对外分发应核对 GPLv3 对应源码、修改说明、第三方通知及签名/品牌/更新来源；这是发布准备，不是当前已证实许可证违规。

## 7. 构建路线、性能与测试现状

### 7.1 三条路线不能混为一谈

| 路线 | 项目 | 特点 |
|---|---|---|
| 当前经典 UWP | Telegram.csproj / Telegram.Msix | Release 使用 .NET Native，当前 fork 操作手册依赖此路线 |
| Modern UWP | Telegram.Modern.csproj / Telegram.Msix.Modern | .NET 10、CsWinRT、NativeAOT；单独 obj/bin 与包身份 |
| Win32/XAML Islands | Telegram.Win32.csproj | 桌面入口与平台条件分支；其脚本明确提示 ARM64 未验证 |

替代项目使用相同源码，不代表同一 runtime、投影、AOT 或窗口语义。Debug 能编译不等于 Release 的 trimming/.NET Native 已验证。本轮没有建议切换发行路线。

### 7.2 性能分析结论

已有性能工作值得保留：类型化事件调用、ConditionalWeakTable 弱订阅、UpdateManager 的 UI 合并派发、媒体状态原子缓存，以及 TdParsers 的 Reader/Pointer 切换和独立 corpus validation。

`Telegram.Benchmarks/Validation.cs` 覆盖 corpus、tokenizer、Reader/Pointer 对照和缓冲区边界；不是“项目完全没有测试”。但这些不能代替账号销毁、导航取消、订阅退役和媒体关闭的并发测试。

本轮没有测量真实 UI 帧时间、长期内存斜率或包启动时间。仓库 README 中的性能数字是作者历史结果，不是本轮实测。建议先验证和测量：

1. 两账号、多窗口来回切换后的存活对象和服务数量。
2. 重复 Gallery/画中画开关后的 RemoteFileSource、ABI 引用与 DisplayRequest。
3. 大量消息/文件更新时的 Dispatcher 排队和合并效果。
4. 大聊天历史加载的分配、UI 帧时间和删除结果合并。

不要为了微小 parse 吞吐提升，先改动高风险协议或 native 所有权逻辑。

### 7.3 推荐补充的最小测试层

- 纯逻辑：配置往返、EOF 边界、有效账号替补、ID 占用。
- 并发：注册/退役屏障、旧历史与删除顺序、Play/Close 引用回收。
- UWP/宿主：来源跳转、异步回复来源绑定、画中画卸载、双账号窗口归属。
- 发布：经典 x64 Debug/Release、完整 MSIX、就地升级及临时测试数据保留；ARM64 或 Modern/Win32 仅在实际计划支持时纳入矩阵。

## 8. 不应误报的行为与未覆盖范围

本轮没有将以下情况直接判为 bug：类很大、async void、catch、必要权限、公开 API 标识、共享代理服务、用户选择保留的下载文件、代码生成输出或快照不含子模块。

已有可靠边界包括：深链接使用 TDLib 分类；代理/手机号等敏感操作有用户确认；附件保存使用系统选择器与目录对象；路径包含逻辑有规范化和分隔符判断；Gallery 常规关闭和聊天离开具有明确解绑链。

仍需动态验证且不计入问题数：

- 授权 task 完成与字段更新顺序、旧授权代次回调回填缓存。
- 快速返回聊天、迟到 Dispatcher 工作、所有历史 Error/短页恢复语义。
- WebView cookie/profile 隔离和退出账号后的浏览器数据策略。
- 安全存储恢复取消分支；修复 StorageId 后需要重新测试。
- Windows 文件系统重解析点、危险扩展名和真实 Launcher 行为。
- 画中画转移失败、WebView 初始化/卸载竞态、弱网范围读取。
- 支付、全部编辑/发送/翻译/内联分支，以及通话全链路。
- TDLib、LibVLC、WebRTC、FFmpeg、libwebp 内部的完整安全审计。

“未发现”只适用于实际检查范围，不作为无缺陷保证。

## 9. 已执行验证记录

### 9.1 EventAggregator 并发探针

目录：`C:\Users\MapleFu\AppData\Local\Temp\unigram-upstream-b6eeb4552-LpdMp0\audit-harness`。

项目直接链接上游 EventAggregator.cs，仅为未测试的 COM 异常识别提供替身。两个工作线程并发取消旧订阅、添加新订阅；全部操作完成后发布，保留订阅对象强引用避免弱引用回收影响。

初测及三次重测均返回：

```text
LOST_SUBSCRIPTION attempt=2 expected=1 actual=0
RESULT attempts=2 lost=1 runtime=.NET 10.0.9
probe_exit=2
```

退出 2 是测试主动报告检测到缺陷，不是构建或工具失败。初测构建成功。该探针是并发压力验证，不是给 race 时间窗口插桩的确定性调度测试；不能将出现次数当成生产发生率。

### 9.2 安全存储配置往返探针

目录：上述快照下 `audit-storage-harness`。

精确提取上游配置类和 JsonSerializerContext 声明，使用虚构账号 1001/1002、bot 777，按原 context 序列化并反序列化；再按原文件名公式计算路径。

```text
CONFIG_ROUNDTRIP accountA_storage_id=<null> accountB_storage_id=<null>
PATH_COLLISION fileA=_777_s fileB=_777_s equal=True
```

构建成功，退出 2 表示检出根因。未调用 Windows Credential Locker、UWP 存储或真实账号；因此这是标识/路径根因验证，不是完整泄露利用测试。

### 9.3 VS MSBuild 配置验证

只执行快照解决方案的 ValidateSolutionConfiguration，不调用 Build.ps1，不编译/打包，不改 manifest。

```text
Release + Platform=x64           → exit 0
Release + Platform=x64|arm64     → MSB4126 / exit 1
```

使用：`F:\Dev\Microsoft Visual Studio\community\MSBuild\Current\Bin\amd64\MSBuild.exe`。

### 9.4 未执行的验证

没有完整编译 Unigram，避免占用另一会话正在使用的工作区、native 输出和打包链。独立探针通过 VS MSBuild 构建，未使用 dotnet build。未安装、退出或重启现有 Telegram 应用。源码快照没有完整子模块和私有构建配置，也不应将它直接当成已准备好的发行构建目录。

## 10. 建议后续修复批次与验收

### 第一批：安全边界

R02 → R03 → R01。

验收：敏感标记绝不进入日志/报告；跨来源页面和迟到回复无法使用原 bot 桥接；所有存储标识有效、账号互不影响且恢复取消生效。StorageId 修复要兼顾旧文件迁移，不能只把 JsonIgnore 删除就宣布解决。

### 第二批：账号与通知可靠性

R04/R05 → R06/R07。

验收：连续退出不留下失效账号；销毁与创建期间目录独占；并发注册/退役不丢订阅。两套订阅系统可以共享设计原则，但不要为修两个窄缺陷引入新的通用事件框架。

### 第三批：媒体与展示

R08 → R13/R12 → R09/R10/R11。

验收：关闭后无未回收 native 源；合法尾字节可读；保屏请求配平；删除不会被旧切片覆盖；账号 B 的窗口和新缩略图不被 A/旧任务影响。

### 第四批：构建与分发

R14，补 ABI smoke test、发布矩阵和依赖清单。R15 已由 fork 处理，重点是防止将来重新引入同名 DLL 的契约漂移。

所有修复均应：先写窄复现测试，只改对应 seam，分别提交，不顺手清理无关上游代码。本轮仅分析，尚未实施任何修复。

## 附录：独立审查产物与恢复索引

工作流：`3cd1523d-b21d-4333-afc1-6c78f83bfb3b`，三个子审查均完成。

产物目录：

`C:\Users\MapleFu\.pi\agent\sessions\--G--Dev-new telegram--\subagent-artifacts\outputs\3cd1523d-b21d-4333-afc1-6c78f83bfb3b\reviews`

- `session-message.md`：账号、缓存、消息与导航；run `1a62a836-662b-44fb-af35-270ed83aa059`。
- `chat-media.md`：Gallery、播放器、文件通知、窗口资源；run `e4548d24-841e-44a6-8d03-0bba82f826a9`。
- `security-data.md`：网页、附件、存储、日志、注销边界；run `7ff0b7a6-f05d-460c-ba59-76a3b556f5db`。

子报告的合并措辞不是本轮的合并批准/阻断决定；本任务没有待合并补丁。以本报告的父会话复核、等级和证据边界为最终分析结论。

快照与探针属于临时产物，后续可清理；这份仓库外报告是持久摘要。保留所有原有工作区未提交改动，未执行 git checkout/reset/clean、提交或推送。
