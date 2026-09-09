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

## 可重复检查

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

已有原版 GPU 截帧的开发者仍可独立进行离线对照，不必把失败的原版 shader Player 当作参考。旧有 `--capture-gpa-camera-and-quit --use-captured-posed-geometry --capture-presented-window --dump-post-inputs` 诊断入口使用恢复的相机和私有顶点流；替换蒙皮网格后会重新绑定描边与前发覆盖命令。经 RenderDoc 启动本地 Player 时，可追加 `--capture-actor-rendering-pass` 捕获该固定姿势下的一帧；日志中的提交数仍需用实际 GPU draw 和缓冲差分核实。所需原版截帧及顶点流不随仓库提供，该入口不影响普通摄影与剧情的姿势。

离线比较须记录分辨率、相机、几何来源、缓冲格式和对齐方式。像素平移应由轮廓遮罩确定，不能用颜色误差选择最有利的偏移；材质内部与边界、无法确定类别的覆盖层应分开统计。角色平均亮度接近，并不代表逐像素渲染已追平。

## 参考与范围

- [截帧分析](https://zhuanlan.zhihu.com/p/1898275837159126787)：管线拆解方法。
- [Shader 逆向](https://zhuanlan.zhihu.com/p/1901300706872394096)：变量、变体和 GPU 调试方法。
- [角色渲染](https://zhuanlan.zhihu.com/p/1908718263602489063)：角色贴图、光照、前发覆盖与描边。
- [作者公开仓库](https://github.com/Yu-ki016/Yu-ki016-Articles)：参考工程与文章；未复制其示例工程、源码或资产。

本实现继续使用既有角色阴影与后处理，并保留此前验证的材质差异。新增功能不等于完整 Live 管线、原游戏逐像素一致、全角色兼容或完整产品验收。
