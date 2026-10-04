# Hot Reactions 审查与修复记录

> 下方 F01–F12 保留修复前的发现和复现证据；原始行号对应提交 `b7c030a6a`，不是当前文件行号。当前处理状态、验证结果和剩余边界见文末。修复限定于排行榜模块及其接入点。

## 原始审查范围与验证边界（修复前）

- 仓库：`G:/Dev/new telegram/Unigram`，分支 `feature/hot-reactions`，提交 `b7c030a6a`。
- 基于 `upstream/develop...HEAD` 确认增量；重点审查四个 HotReactions 服务/模型、侧栏 XAML 和代码、ChatView 接入、DialogViewModel 更新处理；兼容弹窗作辅助检查。
- 没有修改仓库源码；审查前后 `git status --short` 均为空。
- 使用 VS MSBuild，在系统临时目录建立两个 .NET 10 Release 测试程序，直接编译引用仓库原始服务源码。
- `Audit.csproj` 使用 TDLib/存储替身：9 个问题触发场景全部复现；分页替身按本地 TDLib `OrderedMessage.cpp` 中 offset=0 排除锚点的实现建模。
- `DatabaseAudit.csproj` 使用原始四个服务文件及项目相同的 SQLitePCLRaw.bundle_winsqlite3 2.1.11：5 个问题触发场景全部复现。Windows.Storage 替身将路径指向临时测试目录；没有读取真实账号数据或应用数据库。
- 未构建完整 UWP 应用、未连接真实 Telegram、未测 UI 帧率/真机应用启动耗时。合成测试指标不是 UWP 性能指标。

下列源码路径相对 `Unigram/Telegram/`。

## P1：应优先修复

### F01 多账号数据、游标和任务缺少隔离
位置：`Services/HotReactions/HotReactionsService.cs:22-29`；`Services/HotReactions/HotReactionsDatabase.cs:58,109,136`。
数据库固定为应用 LocalFolder/hot_reactions.db，状态键为 chat_id，消息键为 (chat_id,message_id)，后台任务和回调也仅按 chat_id 管理；实际 IClientService 有 SessionId，但本功能没有用它隔离服务或数据。
两个账号访问同一频道时，会共享扫描完成状态、门槛和缓存，互相停止/替换后台任务；账号历史可见性不同还可能显示另一账号曾收录的条目。
真实 SQLite 验证：另一数据库实例直接读到第一实例保存的同 chat_id 条目。跨账号 UI 行为根据服务调用链静态确认，未连接真实账号。
建议：按账号生命周期创建服务及数据库；或为所有数据和任务键加入稳定账号标识。账号删除/退出需停止所属任务并处理缓存。

### F02 消息写入失败后仍推进扫描游标
位置：`Services/HotReactions/HotReactionsDatabase.cs:307-327,788-801`；`Services/HotReactions/HotReactionsService.cs:270-290,554-561`。
SQLitePCL 的 prepare/step 主要返回错误码，并不自动抛异常。Upsert/ExecuteNonQuery 忽略 step 返回码，失败不会触发 catch；调用方随后保存同步状态，ExecuteIfEpochValid 也只是验证代次，不代表数据库提交成功。
真实 SQLite 故障注入：用临时触发器拒绝该频道的 INSERT，消息行数为 0，但 OldestSyncedMsgId 已推进到 123，LastHotSync 同时更新。后续扫描可能越过缺失数据。
建议：检查每次 prepare/step/commit 结果；写入失败不得保存推进后的游标，批次消息与对应状态宜在同一事务提交。

### F03 网络等待期间的旧状态覆盖用户门槛
位置：`Services/HotReactions/HotReactionsService.cs:391-399,554-561,663-670`；`Services/HotReactions/HotReactionsDatabase.cs:232-237`。
慢爬先读取整个 ChannelSyncState，再等待网络，最后 INSERT OR REPLACE 整行；设置门槛是独立的读改写操作，不受同一更新协议保护。写入时加锁无法使网络请求前的快照保持新鲜。
隔离测试：等待慢爬响应时将 CustomThreshold 设为 77；响应落盘后被旧快照覆盖为 null。设置门槛也有反向覆盖扫描游标的可能。
建议：按字段更新用户配置与扫描状态，或在统一的频道串行更新协议下合并最新状态；不要整行覆盖旧快照。

### F04 热区扫描预算截断后没有补全新消息缺口
位置：`Services/HotReactions/HotReactionsService.cs:190-198,281-290,363-366`。
热区每次从最新开始，只取最多 500 条或扫描约 10 秒；已有 OldestSyncedMsgId 不更新，冷爬从历史旧游标继续，已完成的冷爬则直接返回。没有为新增历史或未扫完热区保存补全游标。
隔离测试：旧历史已完成后新增 1100 条消息；热同步只保存 500 条，中间 600 条未收录，冷爬依旧报告完成。反复从最新刷新无法补上预算之外的消息。
建议：保留快速打开预算，但显式保存未完成热区/新增消息区间，将剩余同步放到受限速的后台补全任务。

### F05 用消息日期判定扫描完成会提前结束
位置：`Services/HotReactions/HotReactionsService.cs:516-518,549-552`。
reachedStart 使用“ID 到达起点 OR 日期小于等于起点日期”。消息 Date 只有秒级，同秒多条消息、导入历史的日期与 ID 顺序差异都使日期不能作为完成证据。
隔离测试：起点 ID=1，批次仅扫描到 ID=2，但二者同一时间戳；ColdSyncCompleted 已为 true，ID=1 尚未收录。
建议：日期只用于进度估算；完成必须基于可靠的 ID/分页边界证据。

### F06 反应身份丢失，且清零更新被忽略
位置：`Services/HotReactions/HotReactionsService.cs:110-132,673-682`；`Services/HotReactions/ReactionSentimentService.cs:190-200`。
所有 CustomEmoji 都映射成 ⭐ 后累加，不同单项反应被合并；ReactionTypePaid 未单独处理，沿用默认 👍，污染计数和情绪分类。被合并后的 JSON 无法再为不同专属表情独立配置分类。
隔离测试：两个专属表情分别 50/60，单项最大被算为 110 而不是 60；👍10 加付费反应20被合并成 👍30。
此外，UpdateMessageReaction 对 count<=0 直接返回，热区同步也只 upsert count>0，无法清除已有旧分数。隔离测试：50 变 0 后数据库替身仍为 50。
建议：用 reaction 类型及 custom_emoji_id 保持唯一身份，显示图标与存储键分离；明确 paid 的产品归类；收到权威清零数据时更新或移除已有榜单条目。

### F07 自适应采样不去重，小频道完成路径漏算
位置：`Services/HotReactions/HotReactionsService.cs:229-236,452-458,479-488,549-561`。
热同步反复从最新遍历，SampleCount 未依据消息 ID 去重，同一消息每次打开/刷新都会再次入样。小体量兜底均值只在空批次完成分支计算，正常 reachedStart 完成分支不计算。
隔离测试：频道仅 3 条有反应消息，打开两次 SampleCount=6；另一个三条各10反应的频道完成慢爬后，ComputedThreshold 仍为1，应为9。
门槛错误不仅影响显示，还会影响历史物理剪枝。
建议：采样对应唯一消息，在所有完成路径统一完成小样本计算；清楚定义热区与冷区对同一消息的采样所有权。

### F08 删除更新依赖当前聊天，过期 Upsert 可复活删除条目
位置：`ViewModels/DialogViewModel.Handle.cs:851-855,1139-1143`；`Services/HotReactions/HotReactionsDatabase.cs:298-301,763-771`。
排行榜删除处理放在当前 DialogViewModel 的 chatId 匹配分支；没有打开该频道的对话视图时，已收到的删除更新不会进入本地榜单数据库。已完成的冷历史没有主动回查，因此条目可长期残留。
另一个竞态：网络响应早于删除，但其 Upsert 晚于删除落盘，INSERT OR REPLACE 将 is_deleted 硬重置为0。
真实 SQLite 验证：MarkMessageDeleted 后查询0条，同一旧消息 Upsert 后查询又变为1条。
建议：在账号级更新入口消费已索引频道的删除事件；保留删除 tombstone，普通历史 Upsert 不得清除权威删除标记。

## P2：设计限制与可见行为问题

### F09 降低门槛不会恢复物理剪枝的数据（需产品决策）
位置：`Services/HotReactions/HotReactionsService.cs:497-511,663-670`；`Services/HotReactions/HotReactionsDatabase.cs:350-355`。
当前门槛直接决定冷区准入和删除，降低门槛只修改数值和本地查询，不回补曾被过滤/删除的历史。
真实 SQLite 验证：两条冷消息分数5/20，门槛提高到10再降为1后，只剩分数20的一条。
这是既有物理剪枝策略的必然限制，不是简单的 SQL 查询 bug。应提示“仅作用于已有索引，完整降低门槛需重扫”，或提供补全重扫能力；不能同时承诺任意调低门槛与无需重新扫描。

### F10 爬虫生命周期行为不一致
位置：`Services/HotReactions/HotReactionsService.cs:171-178,301-306`；`Views/HotReactionsPanel.xaml.cs:501-504`；`Services/HotReactions/HotReactionsDatabase.cs:93-94`。
热刷新会停止冷爬，crawlerWasRunning 未用于恢复；OpenAsync 有另行启动逻辑，但手动刷新按钮没有，因此用户点击热区刷新后，慢爬被静默停下。隔离测试复现 runningBefore=true、runningAfter=false。
数据库注释称“一次性修复”，实际上每次 Initialize 都清除全部 cold_sync_completed；真实 SQLite 验证已完成状态从true变false。应采用有版本标识的迁移，避免每次重启重做完成流程。

### F11 分页只有 UI 切片，进度通知触发全量重算
位置：`Views/HotReactionsPanel.xaml.cs:276-280,552-574`；`Services/HotReactions/HotReactionsDatabase.cs:435-447,510-527`；`Services/HotReactions/ReactionSentimentService.cs:190-195`。
每次进度回调均重新加载频道所有候选消息、解析 JSON、生成显示字符串、按分数排序；40条分页仅是最后的内存 Skip/Take。即使是等待、失败、状态变化而没有新增数据也会重算。_loadVersion 丢弃过期结果，但不取消已经运行的工作。
合成测试：5万条候选消息，真实 SQLite + 原始净正面排序，.NET10 Release，预热后连续5次重载；两轮平均分别133.5ms和124.6ms，每次当前执行线程分配约32.4MiB。不是 UWP 真机基准，也不是32MiB常驻内存增长。
后台执行减少 UI 直接阻塞，但并不消除 GC、数据库互斥锁与重复 CPU 开销。实际重排包含 O(N log N) 排序，不能承诺 O(N) 亚毫秒级。
建议：先区分数据变更与状态通知，合并/节流刷新，避免重算无变化榜单；复用 JSON 解析结果，并在 All 模式跳过无用解析。再用 UWP 真机测 CPU、GC、UI响应。

### F12 限流等待被截短，后台请求缺少账号级总预算
位置：`Services/HotReactions/HotReactionsService.cs:26,409-418`；`Views/HotReactionsPanel.xaml.cs:89-95,176-190`。
FLOOD_WAIT 等待秒数被 Clamp 到最多60秒；要求900秒会在60秒后再次尝试。静态确认了裁剪行为，未对真实服务器触发限流。
切换频道或关闭面板只解绑回调，不停止旧频道冷爬，这是后台补全设计；因此多个频道会各自每2.5秒请求，并不存在账号级请求速率上限。固定单频道间隔不能保证总请求量或“绝对安全”。
建议：尊重服务器要求的完整等待时间；为每个账号建立总体并发和请求预算，并明确后台扫描范围。

## 建议修复顺序

1. 账号隔离、SQLite错误处理/批次状态一致性、旧状态覆盖。
2. 热区补全与扫描完成判定。
3. 反应身份/清零、自适应采样、删除事件和 tombstone。
4. 明确门槛降低语义，修复刷新与迁移生命周期。
5. 合并榜单刷新并做 UWP 真机性能采样，验证账号级限流策略。

不建议为这些问题重构全仓库。保留现有模块边界，以有针对性的回归用例保护修复。

## 原始审查证据文件（修复前）

原报告和复现程序保存在 `C:/Users/MapleFu/AppData/Local/Temp/hot-reactions-audit-nmz9g4IV/`；这些是当时原始版本的审查证据，不是修复后回归套件。

- `Program.cs`、`Stubs.cs`、`Audit.csproj`、`service-results.txt`：9个服务逻辑触发场景。
- `DatabaseProgram.cs`、`DatabaseStubs.cs`、`DatabaseAudit.csproj`、`database-results.txt`：5个真实SQLite场景与合成性能测试。
- `bin/Release/net10.0/`：隔离测试程序编译产物。

## 修复计划与已确认的产品决策

- 降低门槛：保留物理剪枝；明确提示并由用户确认后重扫，保留新门槛。
- 付费反应：使用独立身份 `paid`，只参与全部榜，不参与正负/惊愕情绪评分。专属表情以 `custom:<id>` 保存身份，可独立配置情绪分类。
- 旧全局数据库无法可靠判定账号归属，不直接导入新账号索引；保留旧文件作为备份，新账号索引从安全隔离的路径重新建立。

| 阶段 | 问题 | 状态 | 验证 |
| --- | --- | --- | --- |
| 1 数据安全与一致性 | F01 账号隔离、F02 写入与游标原子性、F03 旧状态覆盖 | 已修复 | 账号/同会话身份更换隔离；新服务等待旧服务关闭；消息及状态写入双故障回滚；账号事件失败自动重试；在途请求不覆盖新门槛 |
| 2 扫描完整性 | F04 热区补全、F05 历史完成判定 | 已修复 | 快速扫描 500 条后持久化游标，后台补齐另 600 条；同秒消息及空页均不误报完成 |
| 3 算法与事件 | F06 反应身份与清零、F07 采样、F08 删除事件 | 已修复 | 独立反应身份；50→0→20；采样去重/封顶/64位总和；账号事件入口及首批响应前删除；tombstone 不复活 |
| 4 生命周期与产品行为 | F09 下调门槛确认重扫、F10 刷新及迁移 | 已修复 | 下调门槛事务内二次检查；确认重扫保留门槛及 tombstone；老化数据剪枝；刷新恢复后台任务；初始化保留完成位 |
| 5 性能与限流 | F11 缓存与刷新合并、F12 账号级限流 | 已优化 | revision/单频道缓存/解析与评分复用/刷新合并；账号共享请求预算，完整限流等待，取消的原生请求仍占用并发槽 |

“已修复/已优化”表示代码已实现并通过下述验证，不代表已经完成真实账号 UI 验收。

## 修复后的实现要点

- **F01**：服务归属 `ClientService`，数据库为 `LocalFolder/<sessionId>/hot_reactions_<userId>.db`，情绪配置也随账号隔离。账号关闭时取消生产者并等待其退出后释放数据库；替代服务等待上次关闭完成再初始化。绑定的用户身份变化时旧请求/事件失效，面板也重新绑定。
- **F02–F03**：检查 SQLite open/prepare/step/reset/事务等关键返回码。`BEGIN IMMEDIATE` 后读取最新状态，消息、采样及游标在同一事务提交；配置和扫描状态按各自字段合并。撤掉旧的整行快照写入接口。
- **F04–F05**：保留 10 秒/500 条的前台预算，持久化未完成同步区间；后台优先补近期区间再扫描历史。完成依据最早消息 ID，不依据秒级日期；日期仅用于显示预计进度，空页和无进展保留游标并重试。
- **F06–F08**：身份键与显示图标分离，单项反应不相互累加。权威清零从榜单消失，但保留必要元数据以接收后续正计数。首次扫描前登记频道，删除事件不再依赖当前对话视图，且 tombstone 不参与采样。样本最多 50 个唯一消息，所有完成路径统一计算不足 50 条的均值。
- **F09–F10**：物理剪枝边界持久化。需要回补时必须确认后重扫；即使后台批次在 UI 检查后提高了门槛，事务内也会拒绝未确认的下调。重扫保留自定义门槛及永久删除标记。手动刷新不会静默暂停后台，关闭面板解绑回调，取消/重置后的旧响应不得写库；幂等列迁移不再重置完成状态。旧弹窗复用同一侧栏实现。
- **F11**：区分数据 revision 与状态通知，只合并有必要的刷新；单账号仅保留一个频道的候选/排序缓存，复用 JSON 解析和未变化条目的显示评分，切换分类/门槛/日期窗口时失效重排。UI 查询、配置写入不在 UI 线程同步执行，过期加载会检查取消。
- **F12**：所有榜单历史请求共享账号并发槽与默认 2.5 秒发起间隔，包括创世探针。尊重完整 `FLOOD_WAIT`/`FLOOD_PREMIUM_WAIT`/429 retry-after。TDLib 原生请求不可取消：前台取消/超时后仍保留其并发槽，直至原生回复到达；迟到的限流也生效，迟到的数据不提交。关闭账号会取消等待者，不会等满 900 秒。

## 修复后验证结果

### 回归测试

目录：`notes/hot-reactions-tests/`。测试直接链接当前四个服务/模型文件，使用真实 SQLitePCLRaw 2.1.11 与 TDLib/Windows.Storage 替身，模拟消息 ID 使用 TDLib 的 `id << 20` 形式；分页明确排除锚点。

```powershell
.\notes\hot-reactions-tests\run.ps1 -Benchmark
```

- **63 项断言全部通过**，进程退出码 `0`；含 SQLite 故障注入及事件写入失败重试、账号生命周期、同步补全、反应/采样/删除、缓存失效及限流竞态。日志中的 `expected delete failure` 是有意注入的错误，不是未处理的运行故障。
- 数据库只在系统临时目录创建，程序退出时清理；不连接真实 Telegram，不读取应用账号数据库。
- 测试记录：`C:/Users/MapleFu/AppData/Local/Temp/hot-reactions-regression.log`。

### 完整客户端项目编译

使用 VS 专有 MSBuild 编译 `Telegram/Telegram.csproj`，`Debug|x64`，包括 UWP C# 与 XAML：**成功，退出码 `0`**。未使用 `dotnet build`。此阶段只验证应用项目，后续完整打包与升级见部署记录。

日志：`C:/Users/MapleFu/AppData/Local/Temp/hot-reactions-uwp-build.log`。

`git diff --check` 通过；没有旧 `HotReactionsService.Current` / `ReactionSentimentService.Current` 引用。改动尚未提交。

### 合成性能比较

环境为 .NET 10 Release、真实隔离 SQLite、5 万条候选、净正面榜、固定历史窗口和门槛。旧值是原版本预热后的重复全量重载；新值区分首载、无变化缓存命中和增量重排，不能把缓存命中当成首次加载速度。

| 场景 | 耗时 | 当前执行线程分配 |
| --- | ---: | ---: |
| 原版本重复重载 | 124.6–133.5 ms | 约 32.4 MiB/次 |
| 修复后首次建立缓存 | 192.86 ms | 此次未统计 |
| 修复后无数据变化重复查询（20 次均值） | 0.064 ms | 0.382 MiB/次 |
| 修复后新增 100 条后重排 | 13.62 ms | 1.442 MiB |

表中是完整客户端编译结束后单独运行回归程序的最后一次测量。数次运行中缓存查询约 0.06–0.08 ms，增量重排约 9–15 ms；同时编译时首载最高约 262 ms，机器负载会影响绝对值。首次建立缓存仍有可见成本；缓存增加常驻内存，这是避免反复解析/分配的取舍。分页仍为内存切片，发生数据/模式变化时仍需遍历候选并进行 `O(N log N)` 排序；未宣称所有情况亚毫秒，也没有用分配量代表常驻内存。以上不是 UWP UI 帧率或真机响应时间。

## 尚待真实客户端验收

- [x] 完整打包、就地升级及启动冒烟：`12.10.5.16 → 12.10.5.17`，签名有效，安装状态 `Ok`，新版进程正常响应。
- [x] 确认 PackageFamilyName 未变化，原 `LocalState` 目录及创建时间保留；没有卸载、清空数据或登出。
- [ ] 登录态与聊天缓存的业务层人工确认；检测到 2 个账号独立索引文件，但没有读取真实数据库内容或操作聊天界面。
- [ ] 多账号切换及重新登录后榜单、门槛、情绪配置相互隔离；旧全局索引保持备份，不自动导入。
- [ ] 打开/关闭侧栏、切换频道、连续点击定位、兼容弹窗、确认/取消重扫等 UI 行为。
- [ ] 真实 TDLib 分页/权限边界以及后台长时间运行；不能用模拟测试承诺完全避免服务端风控。
- [ ] UWP 下测首次加载、分类切换、UI 帧率、GC 和常驻内存，评估缓存与后台补全的实际收益。

## 打包与部署记录（2026-10-04）

- 将 `Telegram.Msix/Package.appxmanifest` 版本从 `12.10.5.16` 递增为 `12.10.5.17`；包名称及发布者不变。
- VS MSBuild 编译 `Telegram.Msix/Telegram.Msix.wapproj`，`Debug|x64`，完整生成已签名 MSIXBundle，退出码 `0`。
- 包路径：`Telegram.Msix/AppPackages/Telegram.Msix_12.10.5.17_Debug_Test/Telegram.Msix_12.10.5.17_x64_Debug.msixbundle`，大小 `72,948,749` 字节，包含 x64 主包及 33 个资源包。
- 安装前核对 Bundle Identity、Publisher、Version 及有效签名，与已安装客户端一致；通过 `Add-AppxPackage -ForceApplicationShutdown` 就地升级，没有卸载或更换包身份。
- 安装后实际版本为 `12.10.5.17`，PackageFamilyName 保持 `38833FF26BA1D.UnigramPreview_g9c9v27vpyspw`，状态 `Ok`。原 `LocalState` 路径和创建时间不变。
- 启动客户端并等待 10 秒；检测到执行路径位于 `12.10.5.17` 安装目录的 `Telegram.exe`，进程正常响应。只检查数据文件元信息，检测到 2 个账号独立索引；未读取登录凭据、账号数据库或聊天内容，也未主动触发历史慢爬。
- 旧全局排行榜索引不自动导入账号隔离的新索引，首次使用新版榜单可能需要重新扫描；这是此前确认的安全迁移策略，不表示聊天数据丢失。
- 打包日志：`C:/Users/MapleFu/AppData/Local/Temp/hot-reactions-package-12.10.5.17.log`。
- 升级前后元信息：`C:/Users/MapleFu/AppData/Local/Temp/hot-reactions-before-update.json`、`hot-reactions-deployment-result.json`；部署脚本为同目录下 `hot-reactions-update-client.ps1`。

当前已安装新版；多账号切换、榜单交互和长期运行仍需按上方清单继续验收。
