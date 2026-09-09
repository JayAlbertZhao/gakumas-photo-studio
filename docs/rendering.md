# 角色渲染与验证

普通摄影模式使用 Built-in 管线中的复现 shader，不要求运行原版 shader。模型、贴图和数据由使用者自行准备；本仓库不提供它们。

## 渲染控制

启动摄影模式后按 **F8** 打开角色渲染面板。它独立于动作、面部和物理调度。

- 描边：使用模型切线中的平滑法线及打包顶点色中的宽度、颜色、深度偏移；保留材质禁用描边的标记。
- 眼部前发覆盖：只在眼白/眉眼的 stencil 区域补画头发。Base 的 A 是视角淡出遮罩，配合材质 `_FadeParam` 与动画头部方向控制透明度，并非直接当作 opacity。其他区域保留不透明头发的深度与混合规则。
- Override story lighting：临时覆盖光照；关闭后恢复剧情/摄影参数。可调相机相对或世界空间主光、明暗边界、阴影颜色混合强度、光滑度、环境光和附加光强度。
- Toggle two test lights：添加/移除两盏演示点光源。Unity 场景中的点光和聚光会被角色采样，最多取距离角色最近的 8 盏；不包含灯光 cookie 或附加灯阴影。

Inspector 中 `ActorRenderControls` 还提供有色主光、阴影乘色/加色、有色边缘光、附加镜面光强度和描边宽度。描边默认宽度是摄影棚设置，不宣称适用于原游戏所有镜头。

材质的 Layer 使用第二套 UV：贴图左半部控制基础色，右半部控制 Definition；同一个权重同时作用于两部分。Definition 的 G 为光滑度，B 为金属度（面部用作三角区遮罩），A 为高光强度。禁用 Definition 贴图时使用材质常量。

剧情背景的 `actorProfile` 中的 `matCapOffset`、`matCapSmoothScale`、`shadeApplyRatio`、`giScale`、`additiveLightScale`、`additiveLightSpecularScale`、`eyeHighlightColor` 已连接至复现 shader；阴影加色和边缘光 RGB 不再被丢弃。

## 无资产 GPU 自检

完成普通 Player 构建后，不需要配置模型、贴图或数据目录，即可运行：

```powershell
& .\unity\output\KotonePhotoStudio.exe --self-test-actor-rendering .\LocalAssets\synthetic-check
```

该入口在读取资产之前进入隔离测试，只生成一个四边形和灰度 ramp，使用实际角色 shader 渲染到浮点缓冲。它检查同向头部/表面法线在三个明暗偏移下是否一致，并用明暗响应正控制拒绝空画面或恒定输出。结果写入 `actor-synthetic.json`，附六张预览 PNG；通过退出 0，失败退出 2。数值检查读取浮点像素，PNG 只用于查看。需可用 GPU 和普通 Built-in Player，不支持 `-nographics` 或原版 shader 研究构建。

这一自检覆盖过一个实际回归：面部三角区曾固定使用默认偏移，即使 F8 或剧情已改变明暗边界。现在头部与公共表面使用同一偏移参数，默认值保持不变。自检不证明真实模型、动态遮挡、完整后处理或动画已正确复现。

自检还检查超采样输出的目标归属：正常相机使用窗口呈现目标，临时离屏截图使用自己的目标，截图结束后保留正常相机注册。此前后处理会把离屏截图的最终颜色写入窗口目标，导致保存的 PNG 全黑，即使角色 HDR 已正确渲染；现在只在相机仍使用超采样自有输入目标时重定向输出。该检查不涵盖隐藏窗口或无图形设备的截图。

## 带资产的可重复检查

先完成常规配置和构建。在已设置 `GAKUMAS_PHOTO_STAGING` 的终端运行：

```powershell
& .\unity\output\KotonePhotoStudio.exe --photo-mode --hide-ui --validate-actor-rendering .\LocalAssets\render-check
```

探针保存 32 张 PNG：近景正面/侧面、描边与前发覆盖开关、明暗边界、世界空间光照、点光/聚光、Layer、环境光、有色边缘光以及恢复动画后的序列帧，并输出 `rendering-probes.json`（schema `photo-studio.actor-rendering-probes.v2`）。需要自己的兼容数据；不是无资产测试，也不是逐骨骼关键帧编辑器或正式导出器。

静态检查阶段暂停动作，每次截图前清空后处理的时域历史，避免前一组光照或镜头污染下一张图；正常播放的累积逻辑保持不变。以 `01-front` 为基准，逐像素比较三次应当相同的画面：

| 检查 | 图像 | JSON 字段 |
| --- | --- | --- |
| 不改变状态的重复截图（A/A） | `01b-front-repeat` | `repeatChangedPixels`、`repeatMaxChannelDifference` |
| 关闭临时光照覆盖，恢复原参数 | `11-profile-restored` | `profileRestoreChangedPixels` |
| 移除测试灯，恢复原场景 | `13-point-lights-removed` | `lightRemovalChangedPixels` |

三组变动像素均为 0 才通过重复性检查；不一致或必需 pass 缺失时 Player 以退出码 2 结束。缺失、被截断的 JSON 不能算通过。该检查只证明本次进程中这些静态状态的恢复，不证明跨机器一致性或功能的视觉正确性。

当前仍观察到少量单通道 `1/255` 级静态差异，严格重复性尚未全部通过；探针保留这些失败，不用容差把它们改记为零。暂停动画时间也不代表所有程序化骨骼求解停止，因此暂不承诺确定性导出。

需要定位差异时追加 `--trace-rendering-stability`。它在正面、正面重复和恢复光照三张图旁保存 ActorData、当前 HDR、时域结果、模糊及 bloom 的原始缓冲和尺寸元数据，用于区分差异发生在哪个阶段；文件体积较大，只应保存在被忽略的本地目录。每张图的 `actorPoseDigest` 是当前进程内角色层 Transform 世界矩阵与蒙皮 blendshape 权重的 SHA-256 指纹。它包含实例 ID，不能跨进程比较，也不包含静态网格顶点或材质状态；指纹变化本身不能证明可见姿势或画面发生变化。新增追踪不冻结或改写骨骼求解。

检查实际图像和 pass 数，不能仅以进程退出码或“画面不黑”验收。眼部遮挡还应选一个睁眼表情，检查侧视时眼睛、高光与前发的关系。不同机器上的动画时刻、动态解算和时域累积不保证逐像素确定性。

新增 pass 使用独立的 `ActorSupplemental.shader`，由相机在透明物体之前按“描边 → 眼部前发覆盖”执行。不对每个身体/眼睛材质重复执行头发 pass。这个拆分也避免已安装 URP 的构建裁剪器删除 Built-in shader 中的自定义 LightMode。

## 原版 shader 研究入口的限制

`--original-shader --original-urp` 仅用于本地诊断，不是面向用户的替代渲染器。专用构建入口为 `GakumasPhotoMode.Editor.PhotoStudioBuilder.BuildOriginalShaderReferencePlayer`，输出到 `unity/output/OriginalShaderReference/`，不覆盖普通 Player。

研究构建会启用 URP 以保留其 shader，调度自定义 `VLActor` pass，并跳过 Built-in 的 `OnRenderImage` 超采样显示链。仅把 URP 配置放在 Resources 中、然后在 Built-in 构建运行时切换管线，并不足以得到有效对照。

研究入口会检查当前 Actor 材质是否具有预期的具名 `Forward` pass。原始 shader 的名称仍在、`isSupported` 为真，甚至 `SetPass` 成功，都不能排除已回退到错误 subshader。缺少预期 pass 或未找到原版 Actor 材质时会记录实际 pass 列表并以退出码 3 拒绝对照；这项检查通过后仍需要 GPU 截帧验证。研究构建结束后恢复原来的管线和抗锯齿设置，避免影响普通工程。

当前本地 Windows bundle 的 Actor pass 仍出现错误 shader；RenderDoc 已确认，不能把这个入口描述为成功的原版画面重放。需要进一步解决 bundle/引擎/变体的兼容性。普通复现 shader 不依赖该入口。原版截帧、字节码及研究产物不得加入公开仓库。

### 已验证的私有原生绘制参考

独立的 D3D11 参考已实际执行一个原始眼高光绘制，不经过 Unity 的 bundle 导入。它使用未修改的原始 VS/PS 字节码，以及同一次离线截帧恢复的顶点/索引、常量缓冲切片、贴图、逐槽 sampler、光栅、混合、深度/stencil 和绘制前完整目标内容。不能只在清空的目标上画一次就视为等价：加法混合的量化及遮挡依赖原有场景内容。

在该 3840×2160 单帧中，原生绘制后的两个颜色附件及深度/stencil 原始缓冲均与原始回放逐位相同；重复绘制也相同。跳过绘制时保留绘制前缓冲，移除高光基础贴图后高光贡献消失，辅助附件和深度保持不变。四次原生运行的 D3D11 调试层均无错误或警告。原始回放的独立重复捕获也核对了有效状态和资源内容；结构体未使用的 union 尾部不属于有效 GPU 状态。

该绘制的颜色实际写入 `SV_Target1`，格式为 R11G11B10_FLOAT；`SV_Target0` 是 RGBA16F 辅助数据，不能仅按附件编号判断颜色。直接绘制前后差分得到的 470 个贡献像素及其数值，与此前“原始基线 − 关闭眼高光”的 pre-fog 差分完全相同，支持继续使用此前的隔离参考。

这项验证打通了单个原始绘制的可执行参考，尚未覆盖其他材质或完整场景，也没有解决 Unity 原始 bundle 的兼容性。本地复现与该参考仍有已记录的颜色和边界残差。原生研究工具、原始字节码和输入快照均保留在私有工作区，不随公开仓库分发；它们不是使用本仓库的额外依赖。

已有原版 GPU 截帧的开发者仍可独立进行离线对照，不必把失败的原版 shader Player 当作参考。旧有 `--capture-gpa-camera-and-quit --use-captured-posed-geometry --capture-presented-window --dump-post-inputs` 诊断入口使用恢复的相机和私有顶点流；替换蒙皮网格后会重新绑定描边与前发覆盖命令。经 RenderDoc 启动本地 Player 时，可追加 `--capture-actor-rendering-pass` 捕获该固定姿势下的一帧；日志中的提交数仍需用实际 GPU draw 和缓冲差分核实。所需原版截帧及顶点流不随仓库提供，该入口不影响普通摄影与剧情的姿势。

离线比较须记录分辨率、相机、几何来源、缓冲格式和对齐方式。像素平移应由轮廓遮罩确定，不能用颜色误差选择最有利的偏移；材质内部与边界、无法确定类别的覆盖层应分开统计。角色平均亮度接近，并不代表逐像素渲染已追平。

### 固定几何不等于固定材质状态

当前捕获姿势入口替换顶点流，但眼高光的 `_BaseMap_ST` 仍来自本次表情采样。与原始截帧比较之前，必须单独核对材质属性和逐材质 property block；不要把输入状态不同造成的位置偏差直接归因于 shader。

可在固定 GPA 捕获命令中追加 `--captured-material-uv <本地 JSON 路径>`，显式恢复对应材质的 `_BaseMap_ST`。此选项必须同时使用 `--capture-gpa-camera-and-quit` 和 `--use-captured-posed-geometry`，不允许在普通摄影、剧情或其他捕获模式中使用。数据格式如下；名称和数值必须换成自己核实的捕获输入，示意值不代表任何角色：

```json
{
  "schema": "photo-studio.captured-material-uv.v1",
  "materials": [
    {
      "renderer": "MyCapturedRenderer",
      "material": "MyHighlightMaterial",
      "baseMapST": [1, 1, 0, 0]
    }
  ]
}
```

输入上限为 64 KiB、32 个材质项。每项必须唯一匹配角色层中已加载捕获几何的活动 renderer 和其材质名称；四个数值必须有限。程序先验证全部项，再写入逐材质 property block，不改共享材质、贴图或表情数据，保留既有属性。重复/歧义绑定、缺失几何、非法输入或截图前 UV 被其他逻辑改写时，退出 3，拒绝对照。成功时在本地 captures 目录写入 `material-uv-*.json`，记录输入文件 SHA-256 和截图前已验证的 UV 状态。它只恢复这一材质属性，并非完整 GPU 状态重放；使用覆盖后的状态时应读取此报告，不能把面部驱动器缓存的 UV 诊断字段当作最终 property block。

名称以运行时 `renderer.name` 和 `sharedMaterials[i].name` 为准；复现材质的名称通常带有 `__photo-toon` 后缀。使用自己的截图输入保存 JSON，不要把私有捕获数据提交到源码仓库。无资产自检同时覆盖非法/重复输入拒绝、未加载捕获几何的拒绝、属性保留和后续写入检测。

一个私人单帧对照验证了这个问题：保持相机、捕获几何和轮廓配准不变，仅在隔离数据中调整贡献眼高光偏移的表情曲线，使运行时 UV 常量逐位匹配原始捕获。高光覆盖区域 IoU 从 0.618 提升至 0.922；原来的三像素纵向偏差消失，没有追加图像平移或调节亮度。重复输出、ActorData，以及关闭高光后的当前 HDR 均逐位不变，差异被限定到该高光绘制。新的显式 UV 入口使用未修改的表情数据，产生了与该对照逐位相同的当前 HDR 和 ActorData；不传选项时原有结果也保持逐位不变。仍有边界和颜色残差，不能据此宣称 shader 已逐像素追平。

隔离加法绘制应在各自管线中比较“基线 − 仅关闭该绘制”的同阶段缓冲，并保留 A/A 和辅助缓冲不变的控制。宽泛的眼部类别遮罩不等于单独的眼高光覆盖区域；后处理输入名称也不保证阶段相同，必须确认它位于时域累积之前还是之后。不同 HDR 格式的量化差异需要单独分析，不能直接用整个人物平均亮度校准掉。

### 已分离的格式影响与相机误差

私有原生参考进一步保持原始 shader 和其余输入不变，仅把颜色目标从 R11G11B10_FLOAT 换成 RGBA16F，并无损转换绘制前颜色。两种格式各自的基线、重复、跳过绘制和移除基础贴图控制均通过，D3D11 调试层无错误或警告；原格式仍逐位重现截帧。该修改只用于控制对照，未改变公开运行时的目标格式。

在共同的 510 像素区域和原有轮廓配准下，同格式控制使本地/参考平均亮度比从 1.00715 降至 1.00116，但亮度绝对误差均值仅从 0.14656 降至 0.14174，覆盖 IoU 也未改善。目标格式能解释相当一部分平均偏差，不能解释主要空间残差，因而没有追加亮度校准。

原始 VS 的无光栅 stream-output 与本地 RenderDoc 的实际 VS 输出又确认：同一眼高光绘制的 1200 个按三角形顺序展开的顶点具有对应的 UV，但旧相机的屏幕位置相差约 2.658 像素和 −1.650 像素（Y 向上坐标）。历史相机提取把带有偏轴投影分量的行向量当作正交基，用转置代替逆矩阵计算位置；这一假设不成立。整数图像配准不能替代正确的相机输入。

### 显式捕获相机输入

固定 GPA 捕获可追加 `--captured-camera-state <本地 JSON 路径>`，保留独立恢复的完整 view/projection。必须同时使用 `--capture-gpa-camera-and-quit --use-captured-posed-geometry --capture-presented-window`，且实际加载了捕获几何；不接受普通摄影、剧情或其他捕获入口。未传选项时保留旧相机近似值，避免静默改变历史基线。该选项不替代 UV 或其他材质状态输入。

JSON schema 为 `photo-studio.captured-camera.v1`，输入限 8 KiB，仅使用可信的本地数据：

| 字段 | 约定 |
| --- | --- |
| `schema` | 上述 schema 字符串 |
| `width`、`height` | 实际渲染目标尺寸，各为 1–16384；超采样时不是窗口尺寸 |
| `nearClip`、`farClip` | 有限、正数，far 大于 near；由捕获相机状态提供 |
| `worldToCamera` | 16 个有限值，行优先的 Unity CPU view；刚性仿射矩阵，观察方向为相机空间负 Z |
| `projection` | 16 个有限值，行优先的 Unity CPU 透视投影；未经过 GPU Y 翻转或深度转换，最后一行为 `[0, 0, -1, 0]` |

矩阵均按列向量相乘，即 `clip = projection * worldToCamera * worldPosition`。程序由 view 的逆矩阵设置相机 Transform，使 shader 相机位置与显式矩阵一致；固定捕获期间关闭轨道控制器，普通摄影/剧情不受影响。投影矩阵是权威输入，不能用目标宽高比重新生成它；相机的 aspect getter 可能由投影重新计算，也须保留。

非法矩阵、尺寸不符、重复/冲突选项或截图前状态变化时退出 3。渲染帧结束后、保存截图前复核目标、Transform、矩阵和镜头状态，成功写入 `camera-state-*.json`：包含输入 SHA-256、应用后的 CPU 状态及实际 `GL.GetGPUProjectionMatrix(..., true)`/view-projection。该报告不能代替 GPU 顶点或图像验证；私有输入及输出均不应提交。

本轮私人单帧实际验证了上述修正：未修改 VS/PS、捕获几何和 UV，也未追加图像平移或亮度拟合。眼高光的 1200 个 GPU 顶点与原始 VS 对应位置最大相差 **0.00166 像素**，深度最大相差 `1.21e-7` NDC；原来的系统性像素偏移消失。原始大世界坐标浮点运算与局部坐标路径仍不保证逐位一致。

在同一 511 像素对照区域，旧相机沿用原有轮廓配准，新相机直接使用零平移：眼高光亮度 MAE 对原始 R11G11B10 参考从 0.14627 降至 0.02624，对 RGBA16F 格式控制从 0.14146 降至 0.01926。仍有颜色/边界残差，尚未证明整个角色或其他材质逐像素一致。新相机基线重复的当前 HDR/ActorData 逐位一致；不传选项的两项缓冲也与旧 Player 逐位一致。

无资产自检新增 30 项相机解析、应用、输入哈希、后续写入和命令行作用域检查，连同已有 ramp、呈现目标及 UV 检查共 49 项在该 D3D11 Player 中通过。真实错误 schema、目标尺寸和 CLI 作用域输入均退出 3 且未生成截图。本轮也检查了普通离屏摄影、剧情 19.90 秒检查点和 32 张渲染探针；此轮静态重复/恢复/移除灯光的变动像素均为 0，不撤销此前记录的间歇性失败。

本轮验证通过替换私有 Player 中本项目的 C# 程序集完成；沿用已有引擎和 shader，没有重新运行 Editor 导入、shader 编译或完整构建。新的相机模块不含原始捕获数值或字节码。

## 参考与范围

- [截帧分析](https://zhuanlan.zhihu.com/p/1898275837159126787)：管线拆解方法。
- [Shader 逆向](https://zhuanlan.zhihu.com/p/1901300706872394096)：变量、变体和 GPU 调试方法。
- [角色渲染](https://zhuanlan.zhihu.com/p/1908718263602489063)：角色贴图、光照、前发覆盖与描边。
- [作者公开仓库](https://github.com/Yu-ki016/Yu-ki016-Articles)：参考工程与文章；未复制其示例工程、源码或资产。

本实现继续使用既有角色阴影与后处理，并保留此前验证的材质差异。新增功能不等于完整 Live 管线、原游戏逐像素一致、全角色兼容或完整产品验收。
