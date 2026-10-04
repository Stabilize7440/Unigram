# 官方广告屏蔽与频道正则过滤

## 用户选择与范围

- 官方广告：在客户端广告接口入口拦截，不请求、不展示；不修改 Premium 身份或服务端账号设置。
- 普通内容：每个频道独立配置，多条规则任一命中就折叠，可点击占位查看原文。
- 规则按账号持久化，频道之间不共享。
- 之前登记的 14 项上游底座问题仍暂缓修复，本次不是其修复批次。

## 使用

进入频道 → 右上角菜单 → **内容过滤**。

- 总开关控制本频道的正则过滤。
- 可添加、删除、编辑规则，并逐条启用/停用。
- 规则匹配消息正文和媒体说明；相册任一消息说明命中时，整个相册气泡折叠。
- 命中显示“已过滤消息 · 点击查看原文”。展开状态保留在当前消息实例，保存频道规则后重新判定。
- 默认区分大小写，遵循 .NET 正则语法；可用 `(?i)` 忽略大小写、`(?s)` 使 `.` 匹配换行、`(?m)` 使用按行的起止锚点。

示例（独立规则）：

```regex
推广|广告合作|商务合作
```

```regex
(?i)example\.com|another-ad\.example
```

```regex
(?s)不感兴趣的主题.*固定结尾
```

过滤原文中的实际文字，不追加隐藏链接的目标地址，不做图片 OCR 或视频识别。规则可以过滤任意不感兴趣的内容，并不局限于广告。

## 实现边界

### 官方广告

`ClientService.Send` 对以下请求直接回调对应的空结果，`SendAsync` 也走同一入口：

- `GetChatSponsoredMessages`：频道广告和 Bot 顶部广告。
- `GetVideoMessageAdvertisements`：视频播放广告浮层。
- `GetSearchSponsoredChats`：搜索赞助结果；当前没有业务调用，入口已覆盖。

普通请求保持透传。广告不进入 TDLib，也不为未展示内容伪造展示/点击事件。官方屏蔽在本 fork 中固定启用，当前没有单独开关。

Telegram 官方文档要求第三方客户端展示 Sponsored Messages，自用没有明确豁免。技术效果不等于平台合规保证；其他客户端和设备不会因此改变。

### 正则过滤

- `ContentFilterSettings` 缓存编译后的规则；每条最多 2048 字符，每个频道最多 64 条。
- 启用规则的无效表达式会阻止保存，并显示具体错误；停用规则允许留存无效表达式。
- 单次正则匹配超时 20ms；仅将超时的这一条按不命中处理并临时停用，继续检查其他独立规则，直到重新保存规则或新建服务才恢复该表达式。
- 每条正则独立存储，避免大量规则塞进一个 ApplicationData 设置值。
- 气泡折叠只作用于具有聊天 Dialog delegate 的频道消息气泡；原始消息内容、消息 ID、集合、数据库和分页游标保持不变。
- 不拦截 Telegram 删除、阅读、下载等普通接口，不改变会员权限或受保护媒体权限。
- 第一版不净化系统通知、聊天列表预览、独立搜索摘要、排行榜侧栏或导出的文件，也不删除媒体文件。

## 改动入口

- `Telegram/Services/ClientService.cs`：统一广告接口拦截。
- `Telegram/Services/Settings/ContentFilterSettings.cs`：规则存储、缓存、版本、超时。
- `Telegram/Services/SettingsService.cs`：账号级设置与退出清理。
- `Telegram/Views/Popups/ChannelContentFilterPopup.cs`：原生规则编辑弹窗。
- `Telegram/Views/ChatView.xaml.cs`：频道菜单入口。
- `Telegram/Controls/Messages/MessageBubble.xaml(.cs)`：原布局折叠、占位、展开和规则刷新。
- `Telegram/ViewModels/MessageViewModel.cs`：当前消息的展开版本。
- `Telegram/Telegram.csproj`：经典 UWP 新源码注册。

## 验证记录

初次验证：

- `notes/content-filter-tests/run.ps1`：40 项隔离检查全部通过。
- 源码正则 `(a+)+$` 长输入：首次约 19ms 返回不命中，后续为 0ms；这是 .NET 10 控制台验证，不是 UWP runtime 实测。
- VS MSBuild `Telegram.csproj`、Debug/x64：退出码 0，C# 和 XAML 编译成功；存在未使用成员及 PRI 等警告。
- 测试使用内存设置和 fake native client，不访问真实账号或 Telegram 网络。

截至源码验证阶段未执行：Release/.NET Native 构建、完整 MSIX 打包、安装重启、真实账号广告验证、UWP UI 动态回归。后续打包安装记录见下文；编译通过不能代替实机功能验收。

初版完成过模板结构比较。经审查修正后，原普通布局保留在 `NormalContent` 内、原视觉状态组独立放在模板根节点，占位按钮默认隐藏；这仍是结构检查，不是动态 UI 验证。

## 独立审查与修正

- 初次只读工作流：`792d691b-c7cc-4c0c-a06c-af00243fcc77`；审查子任务：`fab10cb4-26bc-4073-bd5d-ece785da5e9b`。
- mission：`ec142a65-67a5-48a7-b046-705171273b7e`。
- 本轮精确差异及构建/测试日志：`C:\Users\MapleFu\AppData\Local\Temp\unigram-content-filter-baseline-ov4606`，基线是开始实现时的工作区内容，不混入其他会话已有排行榜改动。

初次审查指出 1 项 P1、2 项 P2 新增回归，父会话复核后全部接受并修正：

1. **模板视觉状态**：将原 `VisualStateGroups` 移到最外层模板根 `Grid`，不留在 `NormalContent`；五个既有状态保留。
2. **超时规则**：一条超时只停用自身，不再跳过后续必命中规则。
3. **尺寸动画生命周期**：抽出内容清理供容器回收和原地折叠共用，折叠不注销已准备好的动画状态；折叠状态直接跳过尺寸动画，避免隐藏面板归零时执行动画。

修正后验证：

- 49 项控制台断言全部通过，包含超时在前／中间和后续规则命中，以及已注册／未准备／真实容器回收的生命周期标志。
- 3 项源码与模板结构检查通过；生命周期方法直接提取当前源码，但使用控件替身，不是 UWP UI 动态运行。
- Debug/x64 重新编译退出码 0。
- 最新精准差异：`feature-after-review.diff`；日志：`tests-after-review.log`、`build-after-review.log`。

原审查者已完成续审，任务：`fd541ea6-1647-44ae-a79d-36afe9512096`。结论：三项原问题均关闭，未发现其修正引入的确定回归或剩余阻断；源码审查、Debug 编译及当前隔离测试证据层面可交付（OK with notes），不代表完成发布或实机验收。

独立复核报告：`C:\Users\MapleFu\.pi\agent\sessions\--G--Dev-new telegram--\subagent-artifacts\outputs\792d691b-c7cc-4c0c-a06c-af00243fcc77\reviews\content-filter.md`。

尚未验证：UWP 原地折叠／展开与虚拟化、主题视觉效果、实际多窗口并发首次初始化、不同账号排队通知、实际播放器行为及 Release 打包。旧上游问题仍暂缓；提交与推送状态以 Git 记录为准。

## 打包与安装（2026-10-04，用户授权）

- 版本：`12.10.5.17` → `12.10.5.18`；仅递增 manifest 的版本号，包名称、Publisher 和 PackageFamilyName 不变。
- 使用 VS MSBuild 构建 `Telegram.Msix.wapproj`，Debug/x64；完整 MSIXBundle 打包退出码 0。
- 包：`Telegram.Msix/AppPackages/Telegram.Msix_12.10.5.18_Debug_Test/Telegram.Msix_12.10.5.18_x64_Debug.msixbundle`，约 69.6 MiB。
- 安装前核对 bundle manifest 身份与已安装包一致，版本更高；Authenticode 签名状态 `Valid`。
- 包 SHA256：`CB79BA7273E42835E19419628A59A105F9DE8BEC4A9F0B875E67B7D7F541A5D2`。
- 对原包执行 `Add-AppxPackage -ForceApplicationShutdown` 就地升级，没有卸载或删除应用数据。
- 安装后版本确认 `12.10.5.18`，状态 `Ok`；已通过原包的 AppsFolder 入口重启。
- 核对运行进程 PID `81352`，路径为 `C:\Program Files\WindowsApps\38833FF26BA1D.UnigramPreview_12.10.5.18_x64__g9c9v27vpyspw\Telegram.exe`。
- 仅核对安装元数据和进程，不读取实际聊天/账号数据；未对真实频道广告及正则交互执行功能验收。
- 打包和部署日志：上述临时证据目录的 `package.log`、`deployment.log`。
