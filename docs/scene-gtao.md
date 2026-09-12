# 场景 GTAO

`SceneDeferredCamera.screenShadow.gtao` 使用已登记场景的实际网格法线／视深度预通道计算环境可见性，写入现有 RG8 的 G 通道，默认关闭。PDF13／19 与 PPT126 提及 Amplify Occlusion，本包不附带该商业插件。此处按 [Jimenez 等人的 GTAO 技术报告 §4](https://www.iryoku.com/downloads/Practical-Realtime-Strategies-for-Accurate-Indirect-Occlusion.pdf) 的视轴 horizon 积分独立实现，不导入原版 shader 或游戏材质。

```csharp
scene.screenShadow.enabled = true;
scene.screenShadow.gtao = new SceneGtaoSettings {
    enabled = true,
    radius = .7f,          // 世界单位
    slices = 4,
    stepsPerSide = 8,
    maxRadiusPixels = 64,
    strength = 1,
    normalBias = .002f,
    falloffStart = .8f,
    thicknessBlend = 0,
    combineWithCapsules = SceneAmbientCombination.Multiply
};
```

调用方仍须登记 `scene.surfaces` 并按 [场景后端](scene-deferred.md) 的层约定排除宿主重复绘制。GTAO 不读取 ActorData、normal map、材质贴花法线或上一帧深度。不新增相机、历史纹理或全局 shader 状态，也不自动为现有 Photo Studio 开启效果。

## 当前积分与采样约定

以实际投影的观察射线为球坐标轴，在其垂直平面等角选择切片。每个切片沿两个屏幕方向查询场景深度，形成一对最大遮挡 horizon，再对投影网格法线的余弦权重做封闭形式的角积分。实现积分被遮蔽的角区，最后从已知无遮挡值 1 扣除，避免有限切片对孤立斜面的积分偏差。它计算深度高度场的可见区域，不把局部深度差或曲率直接当作 AO。

纹素位置取最近像素中心。屏幕方向由实际视图／投影矩阵的局部导数确定，支持透视、正交和偏移投影。每侧距离从至少一个像素到投影半径按二次分布采样，并受 `maxRadiusPixels` 限制；重建后的实际世界距离还必须小于 `radius`。纹素中心偏离切片平面时，先确认采样点位于真实网格法线的正半球，再投影回切片求角度，避免共面的斜面因纹素取整而错误自遮蔽。超出屏幕、背景、中心自身和无效距离不产生遮挡，不将越界查询 clamp 回边缘。

`normalBias` 沿网格法线移动接收点。半径外贡献为零；`falloffStart` 指定开始将候选 horizon 向无阻挡边界衰减的半径比例，1 表示硬半径截断。`thicknessBlend` 默认为 0，保留最大 horizon；大于 0 时，较远采样发现较低 horizon 会使当前值朝它衰减。这是薄物体启发式，不是实际厚度测量，也不能恢复深度图不可见的背面。零法线、背向或近切线朝向观察射线的法线输出完全可见。

当前每个计算点使用固定切片，无空间随机旋转、历史重投影或抖动。可选半分辨率空间重建见下节，它不增加有效切片方向数。采样仍可能在运动、薄几何、半径硬切换和屏幕边界出现走样；不能用 GTAO 名称宣称完整场景 ground-truth 一致。

## 可选半分辨率空间重建

```csharp
scene.screenShadow.gtao.resolution = SceneGtaoResolution.Half;
scene.screenShadow.gtao.reconstructionDepthTolerance = .02f; // 世界单位
scene.screenShadow.gtao.reconstructionNormalThreshold = .9f; // 法线余弦下限
```

默认仍为 `Full`。`Half` 参考公开技术报告 §4.1 的半分辨率／双边重建策略，采用独立确定性约定，不复刻商业插件内部滤波器：

1. 保留原全分辨率几何预通道，按 GPU 纹理坐标划分 2×2 单元，选择最近正视深度；相等时先 X 后 Y 遍历，保留首点。奇数边长末单元只读取实际存在的像素。全背景单元无接收点。
2. 在选中的**实际全分辨率像素中心**执行原 GTAO，horizon 仍查询全分辨率深度，世界半径和像素上限不减半。粗纹理 XY 存原始整数纹理像素坐标，Z 存正视深度，W 存未量化 GTAO；背景为 `(0,0,0,1)`。坐标遵循模块 shader 的纹理 UV，不应在 CPU 读回时仅凭 `graphicsUVStartsAtTop` 再做一次翻转；渲染到纹理的投影矩阵也参与方向约定。
3. 在连续粗网格坐标附近遍历 4×4 单元。按实际选中点与接收点的像素距离计算每轴半径 4 像素的 tent 权重；再乘双方切平面最大分离距离的线性权重，以及法线点积的线性权重。分离距离到 depthTolerance、点积降到 normalThreshold 时，权重均为 0。跳过越界／背景，归一化有效权重。斜面的原始视深度变化不会直接使同面支持失效。
4. 无兼容样本或权重总和不超过 `1e-6` 时，重新计算当前全分辨率点 GTAO；不借用前景深度或强行输出白色。细孔／交替前景等最坏情况会大量回退，计算量不保证减为四分之一。

主灯 R 和世界胶囊仍全分辨率计算，之后与重建 GTAO 合并，仅最后写 RG8 时量化。引导不读取 normal map 或网格 ID，邻近、共面同法线但拓扑不连通的片段仍可能互相支持；几何阈值不能识别所有对象边界，滤波也会平滑同面 AO 细节。世界缩放时同步缩放 radius、bias 和 depthTolerance。此模式没有时域抗闪烁或历史累积。

## Capsule AO 与光照

GTAO 与胶囊分别先应用自己的强度，再合并进 G。`Multiply` 相乘，隐含二者遮挡近似独立；同一物体同时参与深度与胶囊可能被重复计算。`Minimum` 只保留两者中较暗者，避免简单相乘，但也可能低估来自不同方向的独立遮挡。两种标量组合都不能精确计算角度可见性的并集。

最终光照仍只让基础间接漫反射乘 G。主灯可见性 R、主灯／附加灯直接光、直接光 GI 乘色与 emission 不随 GTAO 统一压暗。当前没有多次反弹补光、GTSO 镜面遮蔽或 bent normal。旧胶囊／主灯模式在 GTAO 关闭时保留原 shader 变体，不新增运行时绘制。

## 资源与输入边界

`Full` 沿用 [ScreenShadow](scene-screen-shadow.md) 的全分辨率 RGBAFloat 几何与 RG8 输出两张私有目标。`Half` 增加 `Frame.gtaoCoarse`：尺寸 `ceil(width/2) × ceil(height/2)`，RGBAFloat，Point／Clamp，无深度附件，另增加一次 fullscreen draw。`ScreenShadowTargetCount` 为 2／3，`GtaoCoarseDrawCalls` 为 0／1；`ScreenShadowResolveDrawCalls` 仍只统计最终 RG8 resolve。额外颜色存储为 `16 × ceil(width/2) × ceil(height/2)` 字节，**没有减少全分辨率预通道或总附件内存**，也没有实测 GPU 加速声明。

全部纹理归相机私有，Frame 仅借用。任何参与目标丢失均使 `Frame.IsCurrent` 失效，下次渲染重建；resize／模式切换／禁用释放不再需要的目标和材质。半分辨率需实际支持 RGBAFloat Render+Sample，失败拒绝场景帧，不静默降级。关闭／零强度 GTAO 不保留粗目标；所有主灯／胶囊／GTAO 贡献均关闭时不分配。

允许 1–16 个切片、每侧 2–32 个深度采样、1–256 像素半径上限；这约束输入，不承诺最大配置适合实时或移动设备。半径须有限且在 `.001..10000`，strength／falloffStart／thicknessBlend 在 `0..1`，bias 在 `0..radius`，组合与分辨率枚举必须合法。`Half` 另要求有限的 depthTolerance 在 `.000001..10000`、normalThreshold 在 `0...9999`。启用时先验证再裁掉零强度；关闭或 null 时忽略未使用参数，`Full` 不验证未使用的重建参数。

P05 的完整范围仍包括时域稳定、全角色接触与移动成本。当前 Built-in 桌面路径没有与 SSR 合并为移动 RenderPass，也未实现 Memoryless/subpass。各阶段实际执行证据见 [渲染记录](rendering.md)，本接口不构成平台和画质验收的替代。
