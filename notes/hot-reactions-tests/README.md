# Hot Reactions 回归测试

对应问题及修复记录：[../hot-reactions-review.md](../hot-reactions-review.md)。

## 运行

要求 Windows、.NET 10 SDK、项目使用的 Visual Studio MSBuild。`run.ps1` 沿用本机的 VS 路径；换机器时调整 `$msbuild`。

在 `Unigram/` 根目录执行：

```powershell
# 正确性回归
.\notes\hot-reactions-tests\run.ps1

# 正确性 + 5 万候选消息合成性能测量
.\notes\hot-reactions-tests\run.ps1 -Benchmark
```

WSL 中可通过 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'G:\Dev\new telegram\Unigram\notes\hot-reactions-tests\run.ps1' -Benchmark` 执行。

当前包含 60 项核心断言；加 `-Benchmark` 时再验证 3 项缓存断言，共 63 项。任何断言失败返回非零退出码。基准数值不设置性能通过门槛，避免把机器负载差异当成逻辑错误。

## 验证内容

- **F01**：账号数据库/配置隔离、会话槽复用、旧全局数据库不导入、服务关闭/替代初始化屏障、同客户端用户身份变化后旧请求失效。
- **F02–F03**：真实 SQLite 触发器拒绝消息或状态写入后，消息/采样/游标一并回滚；事件落盘失败会保留并自动重试；在途扫描不会覆盖用户门槛。
- **F04–F05**：500 条快速预算之外的消息能补齐；同秒消息和临时空页不会误报历史完成。
- **F06–F08**：独立 custom/paid 身份；单项最大计数；50→0→20；去重、50 条封顶及 64 位采样总和；账号事件与首批响应前的删除；tombstone 在旧响应和重扫中均不复活。
- **F09–F10**：未确认的低门槛会被事务内再检查拒绝；重扫保留门槛；老化低分数据剪枝；初始化保留完成位；刷新恢复后台且旧代次响应不能写库。
- **F11–F12**：元数据通知不触发无谓失效；显示快照不被后续更新篡改；增量及分类配置使缓存正确失效；账号总请求间隔、完整限流、迟到限流、不可取消原生请求的并发槽，以及取消长等待的关闭行为。

## 测试边界

项目直接链接 `Telegram/Services/HotReactions/*.cs`，不是复制服务逻辑。SQLite 为真实的 `SQLitePCLRaw.bundle_winsqlite3` 2.1.11；TDLib、客户端及 Windows.Storage 使用小型替身。

- 消息 ID 按 `id << 20` 建模，`offset=0` 排除分页锚点。
- 所有数据库写入系统临时目录 `hot-reactions-tests-<GUID>`，退出时删除。不连接 Telegram，不读取真实账号数据库。
- 63 项断言不等同于 63 个独立测试用例；它们分布在多个场景中。故障注入产生的 `expected delete failure` 日志是预期行为。
- 基准固定历史窗口和门槛，分别报告首次建立缓存、20 次无变化查询均值、新增 100 条后的重排，以及当前线程分配量。
- 缓存命中耗时不是首次加载耗时；分配量不是常驻内存增长；.NET 10 测量不是 UWP UI 帧率。UI、真实分页/权限和应用级性能仍需客户端验收。

完整应用项目应按项目 `AGENTS.md` 使用 VS MSBuild 编译 `Telegram/Telegram.csproj`；本测试项目编译成功不能替代 UWP C#/XAML 编译。
