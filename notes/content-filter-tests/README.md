# 内容过滤隔离测试

运行（Windows PowerShell）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "G:\Dev\new telegram\Unigram\notes\content-filter-tests\run.ps1"
```

- 使用 VS MSBuild，不使用 dotnet build。
- 直接链接应用的 `ContentFilterSettings.cs`；设置存储是内存替身，不访问真实账号。
- 每次从当前 `ClientService.cs` 精确提取 `Send`/`SendAsync` 方法，使用假 native client 验证三类广告接口返回空结果且不发送请求，以及普通接口仍透传。
- TDLib 类型使用最小结构替身；完整应用编译负责检查实际生成绑定的兼容性。
- 覆盖规则持久化、账号/频道隔离、任一命中、开关、复制隔离、版本失效、无效/耗时正则和规则删除；包含超时规则之后仍有规则命中的回归。
- 每次提取真实气泡的内容清理、容器回收和动画注册方法，配合轻量控件替身检查注册标志保留；不是完整 UWP 控件运行。
- 静态检查视觉状态组位于模板根节点、五个原有状态保留，以及原地折叠使用内容清理且折叠尺寸变化不进入动画。
- 当前：49 项控制台断言及 3 项源代码/模板结构检查通过。
- 测试运行不发送网络请求，不安装、不启动 Unigram；构建还原阶段可能访问包源，不等同于 UWP UI 动态回归。

手工 UI 验收：频道菜单 → 内容过滤 → 添加规则 → 保存；命中消息折叠，点击可展开，滚动回收后仍保持展开；保存规则后重新判定。检查关闭频道开关、切换频道/账号、媒体说明、相册、消息编辑、不同窗口实时刷新及无障碍名称。
