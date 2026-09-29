# NativeAOT 部署评估

> 结论先行：**建议先上 ReadyToRun（零代码改动），NativeAOT 列为条件触发项**——
> 本应用的性能重心在 WebView2 与 DSH Runtime（node 子进程），.NET 壳层的 AOT 收益是启动时间
> 与内存的小幅改善，而迁移有明确的硬成本（System.Text.Json 反射序列化 7 处、WMI 待验证）。

## 现状

当前发布为自包含（zip 内含 coreclr/JIT 等运行时，无框架依赖），JIT 执行。发布链路：
`dotnet publish -r win-x64`（linux-x64 次要目标）→ Inno Setup / zip 分发。

## NativeAOT 的收益（对本应用的现实估计）

| 维度 | 预期 | 说明 |
| --- | --- | --- |
| 冷启动 | ↓ 20-40% | 壳层启动 300-600ms 量级；WebView2 与 node 就绪在秒级，占比小 |
| 内存 | ↓ 10-20% | 壳进程自身；WebView2/node 进程不受影响 |
| 分发 | 单 exe（~60-90MB） | 与当前自包含目录体积相近；Inno/自更新流程兼容 |
| 稳态性能 | 无感 | 无服务端/高频计算路径 |

## 迁移硬成本（按风险排序）

1. **System.Text.Json 反射序列化（7 处，必须改）**：NativeAOT 下反射序列化运行时直接失败。
   需为 `AppSettings`/`PersistedSettings`/`RuntimeUpdateTransaction` 等添加 `JsonSerializerContext`
   源生成；`DshEventMonitor.cs:253` 的匿名类型序列化需改为 `JsonNode` 或具名 DTO。
   `JsonDocument`/`JsonNode`（解析/拼装）不受影响。
2. **System.Management（WMI 孤儿进程扫描，需实测）**：包官方未承诺 AOT 兼容（COM 互操作路径，
   大概率可用但无保证）。验证不通过时的替代：退化为仅 pid 文件清理，或改
   NtQueryInformationProcess + ReadProcessMemory（复杂度高，不建议先做）。
3. **WinRT Toast（windows TFM）**：CSWinRT 官方支持 NativeAOT，风险低，需冒烟验证。
4. **ViewLocator.cs:27 的 Activator.CreateInstance**：仅设计器路径，加特性裁剪或直接移除。
5. **Avalonia 12**：官方支持 NativeAOT（XAML 已编译），风险低。
6. **测试矩阵**：托盘/原生 Toast/全局热键/WebView/自启动/`--apply-update` 自更新全量冒烟，
   两个 TFM × 两个 RID。

## 建议路线

1. **已实施（2026-09）**：发布已启用 `<PublishReadyToRun>true</PublishReadyToRun>`（见 DSHSharp.csproj），
   零代码改动拿走大部分启动收益；建议在下一次发版时实测冷启动对比数据。
2. **触发条件再上 NativeAOT**：出现明确的启动时间/内存目标，或需要单 exe 分发形态。
   迁移工作量估计 2-4 天（含上面 1-2 项与全量冒烟），拆分：
   - 第 1 天：JsonSerializerContext 迁移 + 单测全绿；
   - 第 2 天：AOT 试构建，WMI/Toast/热键专项验证；
   - 第 3-4 天：全矩阵冒烟 + 修尾（含 linux-x64 WebKitGTK）。

## 与近期健壮性工作的关系

环境预检（Node/WebView2 自动修复）全部走进程/注册表/HTTP，无反射路径，与 AOT 兼容；
若未来上 AOT，`WebView2RuntimeService` 的 PowerShell 签名校验子进程调用不受影响。
