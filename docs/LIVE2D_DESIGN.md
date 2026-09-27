# DesktopPet Live2D 显示设计方案

> 实施状态（2026-09-27）：已接入用户提供的 Cubism SDK for Web 5-r.5，完成 schema 2、安全导入、透明 WebView2 渲染、Idle/Click/Dragging 动作映射、逐参数全局鼠标追踪、首帧确认与故障回退。已用 SDK 自带 Hiyori 模型通过透明 WPF 窗口首帧冒烟测试；发布许可、多 DPI/多显示器和长时间稳定性仍需发布前专项验证。

## 1. 目标与结论

在保留现有 PNG 桌宠的前提下，新增可切换的 Live2D 桌宠渲染器。用户从本机导入自己的模型，应用将模型复制到 `%LocalAppData%/DesktopPet/assets/pet/packages/<guid>/`，以后不依赖原始路径，也不读取仓库 `art/` 目录。

首版推荐采用以下技术路线：

- WPF 继续负责透明桌宠窗口、拖动、缩放、右键菜单和状态机。
- `WebView2CompositionControl` 作为 `IPetRenderer.View`，在本地离线页面内运行官方 Cubism SDK for Web。
- C# 与渲染页只通过小型 JSON 消息协议通信；模型数据不能执行脚本，也不能访问网络。
- Live2D 包以 `.model3.json` 为入口，连同其引用的 `.moc3`、纹理和可选动作、表情、物理文件一起导入。
- PNG 渲染路径保持不变；WebView2 或模型加载失败时保留当前桌宠，启动失败则回退到内置 PNG。

该路线比在 WPF 内新增 C++/DirectX 互操作层更适合第一版：它能复用官方 Web Framework 的模型、动作和物理实现，`WebView2CompositionControl` 也避免普通 `HwndHost` 的 WPF airspace 问题。透明分层窗口兼容性仍须先通过第 3 节的技术验证门；验证失败时才切换到“Cubism SDK for Native + Direct3D 11 + D3DImage/共享纹理”的备选路线。

## 2. 素材格式边界

### 2.1 支持的导入内容

导入对话框提供两种入口：

1. 选择 `.model3.json`（推荐）；程序解析引用关系并复制完整资源集合。
2. 选择 `.moc3` 时，如果同目录恰好有一个 `.model3.json`，程序自动使用该入口；否则提示选择对应的 `.model3.json`。

包内基础文件如下：

| 文件 | 要求 | 用途 |
| --- | --- | --- |
| `*.model3.json` | 必填且唯一 | 模型入口和资源索引 |
| `*.moc3` | 必填且由入口引用 | 运行时模型数据 |
| `*.png` | 至少一张且由入口引用 | 纹理图集 |
| `*.motion3.json` | 可选 | Idle、Click、Dragging 动作 |
| `*.exp3.json` | 可选 | 表情 |
| `*.physics3.json` | 可选 | 物理效果 |
| `*.pose3.json`、`*.userdata3.json`、`*.cdi3.json` | 可选 | SDK 支持的附加数据 |

仅选择一个 `.moc3` 通常不足以显示模型，因为运行时还需要纹理，动作和物理也由其他文件描述。因此首版不把“裸 `.moc3`”作为可保存的包；选择它时在同目录查找唯一的 `.model3.json`，找到后让用户确认改用该入口，找不到则说明缺失文件。

### 2.2 `.cmo3` 的处理

`.cmo3` 是 Cubism Editor 的可编辑工程，不是 SDK 运行时格式，应用不尝试解析或转换。用户选择 `.cmo3` 时显示导出指引：

> 请在 Cubism Editor 中选择“文件 → 导出嵌入用文件 → 导出 moc3 文件”，然后导入生成的 `.model3.json` 所在目录。

官方文档说明 `.moc3` 是程序使用的模型数据，而 `.model3.json` 用于关联 `.moc3`、纹理和其他资源；Editor 默认会同时导出 `.moc3`、`.model3.json` 和纹理。参见 [Data for Embedded Use](https://docs.live2d.com/en/cubism-editor-manual/export-moc3-motion3-files/) 和 [File Types and Extensions](https://docs.live2d.com/en/cubism-editor-manual/file-type-and-extension/)。

### 2.3 不在首版范围内

- 不编辑、反编译或自动修复 `.cmo3` / `.moc3`。
- 不提供摄像头面捕、麦克风口型同步或 AI 对话。
- 不从 URL、模型市场或压缩包直接导入。
- 不支持 Cubism 2 的 `.moc` / `.model.json`。
- 不把仓库 `art/` 中的任何文件打包、扫描或作为示例模型。

## 3. 技术验证门

正式改造前先做一个不进入产品分支的最小验证：在当前 `AllowsTransparency=True` 的 WPF 窗口中放置 `WebView2CompositionControl`，加载官方示例模型并检查以下项目：

- 页面和 WebView2 默认背景均为全透明，无白边、白闪或黑底。
- WPF 的缩放框能够覆盖在模型上方，鼠标拖动、右键菜单和点击仍由 `PetRoot` 接收。
- 100%、150%、200% DPI 及跨显示器移动时，模型无裁切、错位或模糊放大。
- 窗口隐藏/恢复、切换 PNG/Live2D、休眠唤醒和显卡设备重置后可恢复。
- 典型模型连续运行 30 分钟，窗口空闲时 CPU、GPU 与内存无持续增长。

Microsoft 文档说明 `WebView2CompositionControl` 是非 `HwndHost` 的 WPF 控件，用于解决 airspace 问题；WebView2 支持完全透明的默认背景，但不支持半透明默认背景。参见 [WebView2 in WPF apps](https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/wpf) 和 [DefaultBackgroundColor](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controller.defaultbackgroundcolor)。

若透明分层窗口在目标机器上不能稳定组合，则停止 WebView2 产品化工作，改用官方 Cubism SDK for Native 的 Direct3D 11 渲染器，通过 C++ DLL 封装 `LoadModel / Resize / Update / Render / SetState / Dispose`，再以共享纹理呈现到 WPF。业务层的包格式、导入验证、状态映射和 `IPetRenderer` 边界不变。

## 4. 运行时架构

```text
MainWindow / PetStateMachine
          |
          v
   IPetRendererFactory
      |            |
      v            v
PngPetRenderer  Live2DPetRenderer (C#)
                       |
                       v
          WebView2CompositionControl
                       |
               JSON 消息桥（白名单）
                       |
                       v
        本地 renderer.html + Cubism SDK for Web
                       |
                       v
             已复制并验证的模型包
```

新增或调整的核心类型：

- `PetDefinition`：不再假设每种桌宠都有 `IdleImagePath`，改为包含按 `rendererType` 区分的设置对象。
- `PetPackageManifest`：统一解析顶层清单，`rendererSettings` 分别反序列化为 PNG 或 Live2D 配置。
- `PetPackageValidator`：负责路径归一化、文件签名/JSON/大小/数量验证和完整引用闭包检查。
- `Live2DPetImporter`：在临时目录完成复制与验证，再用现有“临时目录 → 正式目录”的原子安装方式提交。
- `Live2DPetRenderer`：创建、初始化和释放 WebView2；实现状态消息、暂停、恢复和故障上报。
- `Live2DPreviewService`：模型首次成功加载后生成 `thumbnail.png`；生成前设置列表显示通用 Live2D 图标。
- `IPetRendererFactory`：由可替换服务创建渲染器，便于隔离 WebView2 初始化失败并进行测试。

`IPetRenderer` 建议扩展为异步生命周期：

```csharp
public interface IPetRenderer : IAsyncDisposable
{
    FrameworkElement View { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    void Show(PetState state);
    void SetVisible(bool visible);
}
```

`MainWindow.TrySelectPet` 相应改为异步“两阶段切换”：新渲染器初始化并显示首帧成功后才替换旧渲染器；失败则销毁新实例并保持旧桌宠，避免切换时出现空窗口。

## 5. 宠物包清单

继续使用当前包目录与 GUID ID，但将清单升级为 schema 2；schema 1 PNG 包保持兼容读取。

```json
{
  "schemaVersion": 2,
  "id": "32位小写十六进制 GUID",
  "name": "示例 Live2D",
  "rendererType": "live2d-cubism",
  "rendererSettings": {
    "model": "model/example.model3.json",
    "thumbnail": "thumbnail.png",
    "fit": "contain",
    "scale": 1.0,
    "offsetX": 0.0,
    "offsetY": 0.0,
    "motions": {
      "idle": "Idle",
      "click": "TapBody",
      "dragging": "Dragging"
    },
    "pointerTracking": [
      { "parameterId": "ParamAngleX", "impact": 100, "reflect": false, "source": "mouseLeftX" },
      { "parameterId": "ParamAngleY", "impact": 100, "reflect": false, "source": "mouseLeftY" },
      { "parameterId": "ParamBodyAngleX", "impact": 100, "reflect": false, "source": "mouseLeftX" },
      { "parameterId": "ParamEyeBallX", "impact": 100, "reflect": false, "source": "mouseLeftX" },
      { "parameterId": "ParamEyeBallY", "impact": 100, "reflect": false, "source": "mouseLeftY" }
    ]
  }
}
```

其中 `motions` 的值是 `.model3.json` 中的动作组名，而不是任意文件路径。组不存在时的确定性回退规则：

- Idle：播放指定 Idle 组；没有则只运行呼吸、眨眼和物理。
- Click：播放指定组一次；没有则保持 Idle。
- Dragging：循环指定组；没有则保持 Idle。
- 从 Click/Dragging 返回 Idle 时淡出当前动作并恢复 Idle，不重建模型。

`fit/scale/offsetX/offsetY` 用于解决不同模型画布边界差异。首版编辑界面提供实时预览、缩放和 X/Y 偏移；窗口本身仍保持现有 2:3 比例。

## 6. 导入与编辑流程

设置页的“添加自定义桌宠”先让用户选择“PNG 图片”或“Live2D 模型”。Live2D 流程如下：

1. 选择文件夹或 `.model3.json`；若选择 `.moc3/.cmo3`，按第 2 节给出纠正指引。
2. 在源目录只读解析入口及其引用闭包，显示模型名称、资源数、总大小和警告。
3. 输入桌宠名称，在隔离预览中调节缩放、偏移以及 Idle/Click/Dragging 动作组映射。
4. 将闭包内文件复制到同级临时包；保留相对目录结构，写入 schema 2 `manifest.json`。
5. 从临时包再次加载并等待首帧；成功后原子移动到正式 GUID 目录，失败则清理临时包。
6. 切换到新桌宠并异步生成缩略图。

导入窗口可打开鼠标追踪设置，逐项配置参数 ID 对应的影响度、反转和鼠标 X/Y 类型。已导入的 Live2D 可从外观列表的“编辑”再次调整并立即重载；设置保存在包清单中。缺少 `pointerTracking` 的旧 schema 2 包自动使用上述五项默认值，空数组表示全部关闭。替换模型资源仍需完整重走导入验证，不能把新旧资源混合覆盖。

删除提示改为“删除应用保存的模型副本”；不删除用户原始模型。删除当前项前仍先切回内置 PNG。

## 7. 验证与安全

导入内容视为不可信输入：

- 所有引用必须是包根目录内的相对路径；拒绝绝对路径、`..` 越界、符号链接/重解析点和大小写碰撞。
- 只复制入口可达且扩展名在白名单内的文件；拒绝 HTML、JS、EXE、DLL、字体和未知文件。
- 建议上限：单文件 64 MB、纹理单张 8192×8192、文件数 512、解包后总量 256 MB、JSON 深度 64。
- 验证 PNG 签名和实际解码尺寸；验证 JSON 可解析、`.moc3` 存在且版本可由当前 Cubism Core 读取。
- WebView2 只加载应用随包发布的渲染页。模型根通过虚拟主机映射为只读资源；禁止外部导航、新窗口、下载、剪贴板、摄像头、麦克风、拖放、上下文菜单和 DevTools（Debug 构建可例外）。
- 设置 CSP：`default-src 'none'`，仅允许随应用发布的脚本以及当前模型虚拟主机的图片/XHR；网络请求统一拒绝。
- Web 消息只接受固定结构的 `ready/loadResult/state/fault`，校验来源 URI、类型和字段范围。
- 模型错误写入本地日志，但不记录模型二进制或用户目录之外的内容。

## 8. 生命周期与性能

- 同一时间只保留当前 Live2D 的一个 WebView2/模型实例；切换后及时释放旧实例。
- 桌宠隐藏、锁屏、远程会话断开时暂停 RAF；显示时恢复并用新的时间戳继续，避免动作跳跃。
- 默认前台 60 FPS；连续无交互 30 秒后降为 30 FPS，点击或拖动恢复 60 FPS。后续可增加省电开关。
- Resize 只更新画布和投影矩阵，不重新加载模型；DPI 变化按实际像素尺寸更新 canvas。
- WebView2 进程异常退出时最多自动重建一次；仍失败则回退内置 PNG 并给出非阻塞提示。
- 应用退出先停止动画循环和消息，再释放 CoreWebView2，最后删除本次会话的临时目录。

## 9. 依赖与发布

产品依赖包括：

- `Microsoft.Web.WebView2` NuGet 包及 Evergreen WebView2 Runtime 检测/安装说明。
- 官方 Cubism SDK for Web 的 Framework 和 Cubism Core；Core 不在公开 GitHub Framework 仓库中，需要按官方许可下载并纳入受控依赖流程。官方仓库说明见 [CubismWebFramework](https://github.com/Live2D/CubismWebFramework) 与 [CubismWebSamples](https://github.com/Live2D/CubismWebSamples)。

许可是发布阻断项，而不是实现后的补充事项。该功能允许最终用户导入数量不定的模型，可能符合 Live2D 所定义的“Expandable Application”；官方页面说明此类应用即使由个人或小规模主体发布，也需要事前审核和专门的 Publication License Agreement。开始发布集成前应由发布主体向 Live2D 确认分类与条款，并记录批准结果。参见 [Expandable Applications](https://www.live2d.com/en/sdk/license/expandable/) 和 [SDK Release License](https://www.live2d.com/en/sdk/license/)。模型本身的版权与使用许可也由用户负责，导入界面应展示相应提示。

## 10. 实施阶段与验收

### 阶段 A：可行性验证

- 完成第 3 节透明窗口、输入、DPI 和恢复测试。
- 确认 SDK 发布许可路径。
- 结论为 WebView2 继续或切换 Native，不在两条渲染路线之间并行开发。

### 阶段 B：包与导入

- [已完成] 清单 schema 2、schema 1 兼容读取、引用闭包验证和原子导入。
- [已完成] 已新增 Live2D 导入 UI、错误提示、缩放/偏移、动作映射、逐参数鼠标追踪设置与 `.cmo3` 导出指引；已导入包可二次编辑鼠标追踪设置。
- 单元测试覆盖路径越界、缺文件、超限、损坏 JSON/PNG、动作组缺失和事务回滚。

### 阶段 C：渲染与交互

- [已完成] WebView2 透明承载、离线资源响应、网络/权限封锁、官方 Core/Framework 适配器、状态消息、首帧确认和故障回退已实现。
- [已完成] 宿主约每 33 ms 采样全局鼠标位置，按照官方示例以画布高度为统一尺度转换 X/Y，并驱动 Cubism `setDragging`；只有位置实际变化时发送消息。运行时根据 MOC3 参数范围、清单中的影响度与反转值生成逐参数追踪系数。
- [已验证] SDK 自带 Hiyori 模型在透明 WPF 测试窗口成功加载并回传首帧 ready。
- PNG 与 Live2D 互相切换至少 50 次无资源持续增长。
- 点击和拖动行为与当前 PNG 桌宠一致，悬浮窗/统计窗联动不回归。

### 阶段 D：发布质量

- Windows 10/11 x64，多显示器 100%–200% DPI 验证。
- 无 WebView2 Runtime、软件渲染、设备丢失、睡眠恢复和损坏当前包的恢复测试。
- 完成第三方许可、Live2D 声明/Logo/商店文案等经许可协议要求的发布材料。

功能完成的最低标准是：用户可从 Editor 导出目录导入模型，重启后继续显示；Idle/Click/Dragging 有确定性表现；原始目录移动或删除不影响桌宠；任何单个坏包都不会阻止应用启动；仓库 `art/` 始终不参与扫描、导入、构建或测试。
