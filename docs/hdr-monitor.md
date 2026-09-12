# HDR Monitor 与发光网格

`HdrMonitor` 用专用相机把 UI／自制场景画入线性 HDR 纹理；`MonitorEmissionMaterial` 让网格按指定 UV 区域采样这张纹理。对应管线 PDF 56–58 页的 Monitor／Emission mesh 设计。仓库只提供独立实现，不包含讲演中的 UI 布局、动画、视频或原版 shader。

这是一条实际的“UI 内容 → 相机纹理 → 多个网格”的链路，不会自动开启剧情场景里禁用的相机，不改已有背景材质或默认摄影画面。核心没有 Photo Studio／UGUI C# 依赖；使用随包提供的 Canvas shader 时，宿主自行准备 UGUI、Canvas 和 RawImage／Image。

## 准备内容与接收网格

1. 新建专用 Camera，取消 Camera 的 Enabled；不要使用主相机。Clear Flags 设 SolidColor，背景通常透明黑，Culling Mask 只包含自己的 Monitor 内容层。添加 `HdrMonitor`，显式打开 monitorEnabled。
2. 用 World Space 或 Screen Space - Camera Canvas，worldCamera 指向该相机，内容放到其捕获层。Screen Space - Overlay 不经过此相机，不能作为输入。
3. UI 元素使用自己拥有的 `GakumasPhotoMode/MonitorCanvas` 材质实例。`_Radiance` 是线性 HDR RGB Vector；例如 `SetVector("_Radiance", new Vector4(4, 1, 0.5f, 1))`。不要依赖 UI 顶点色存储大于 1 的 HDR 强度。纹理、普通顶点色、alpha 与硬边 RectMask2D／Mask 仍可使用。
4. 接收网格使用 `MonitorEmissionMaterial` 创建的材质；模型和 UV 由宿主提供。不同网格可取同一 atlas 的不同部分；不要求把游戏原来的 LED 纹理打包进工具库。

## 显式驱动

```csharp
var emission = new MonitorEmissionMaterial();
var settings = new MonitorEmissionSettings {
    uvChannel = 0, // Mesh UV0..UV3；不是贴图数量
    monitorUV = new Vector4(0.5f, 1, 0.5f, 0), // 取 atlas 右半边
    linearTint = Vector3.one,
    intensity = 1
};
Material previous = surface.sharedMaterial;

// 只在本次真的需要捕获时执行；动画采样也可放在这里。
void Prepare(double seconds) { Canvas.ForceUpdateCanvases(); }
monitor.PrepareCapture += Prepare;
monitor.updateMode = MonitorUpdateMode.FixedRate;
monitor.updatesPerSecond = 30;

// 宿主 LateUpdate 或自己的时间线驱动；不要在相机 render 回调里调用。
if (monitor.TryUpdate(timelineSeconds, contentRevision, out var frame) &&
    emission.TryBind(frame, settings, out var error))
    surface.sharedMaterial = emission.Material;
else
    emission.Unbind();

// 会话结束：恢复自己替换的材质，再解除事件／释放资源。
if (surface.sharedMaterial == emission.Material) surface.sharedMaterial = previous;
monitor.PrepareCapture -= Prepare;
emission.Dispose();
monitor.enabled = false;
```

以上为初始化／每次 tick／销毁三个时机的片段；不能把整段在每帧从头到尾执行。`contentRevision` 是宿主的 `ulong` 版本号，例如脚本改了文本、图像、亮度或布局时递增。多个接收网格分别拥有材质包装器，不写入共享资产；多相机分别使用自己的 HdrMonitor。

| 调度方式 | 何时真正渲染 |
| --- | --- |
| WhenDirty（默认） | 首帧、内容版本变化、RequestUpdate、相机配置／尺寸／资源变化或时间倒退 |
| FixedRate | 上述变化，或距上次捕获达到 `1 / updatesPerSecond`；不补渲染跳过的历史帧 |
| EveryCall | 每次合法 TryUpdate 都渲染，包括同一时间的重复调用 |

`PrepareCapture(seconds)` 只在真正捕获时触发，此时 HDR target 已挂到专用相机，适合更新依赖目标大小的 Canvas 布局。回调只负责内容，不应更改相机配置。回调异常、递归更新和捕获期间禁用都有拒绝／清理路径；捕获中发出的 RequestUpdate 保留到下一次调用。宿主选择 scaled／unscaled／剧情时间，模块不自行推进动画、视频解码或游戏时钟。

`DidRender` 与 `RenderSequence` 可观察跳帧／更新，但不是 GPU 性能计时。修改任意 UI 或材质不会被自动扫描，必须更新版本、请求刷新或使用限频策略。

## 颜色、UV 与所有权

输出为 ARGBHalf、Linear、无 MSAA／mip、Clamp／Bilinear。UI alpha 在捕获时参与合成，发布 RGB 已经是合成后的辐射；网格消费 RGB，不再乘一次 UI alpha。发光材质为 opaque，写深度、输出 alpha=1；不提供半透明玻璃或材质 PBR。最终画面的 Bloom／色调映射仍由宿主后处理决定，模块不自动为周围物体加灯光或 GI。

`monitorUV` 是所选 Mesh UV 的 scale.xy／offset.zw。`linearTint` 和 intensity 显式乘发光，选配 `ledPattern`、ledTiling、ledStrength 可调制 LED 点阵；这是自己的乘色模型，不推断原版点阵参数。消费 RGB 限在 half 浮点非负范围，任意 HDR 源材质也应避免生成 NaN／Infinity。LED 强度为零恢复未调制辐射；源 UI 颜色动画无需重建接收材质。

PDF 58 的图中点阵重复数随 Monitor 纹理宽高缩放。对应的显式配置为 `settings.ledTiling = new Vector2(frame.texture.width, frame.texture.height) * dotScale`，尺寸变化后重绑即可；dotScale 由自己的点阵内容决定，不猜测原资产默认值。当前消费者只产生发光，图中的 Base／normal／MAOS 完整 PBR 表面属于场景材质链，未由此单项替代。

Frame 和 texture 为借用对象，不能写入、释放或更改其过滤／尺寸。Frame.IsCurrent 检查发布序号和当前配置／资源；下一次真正捕获使旧 Frame 失效，静态跳过则保留。尺寸不变时发布纹理对象稳定，重建、禁用或销毁时失效。消费失败应调用 Unbind／TryBind 的无效帧路径清黑，不能继续当作新内容使用。

模块拥有两张独立的 HDR 纹理：带 24-bit depth/stencil 的相机 capture，以及稳定的 published 颜色纹理；捕获后复制发布，避免源 UI 读取正在写的同一 attachment。允许显式设计“读取上一发布帧”的反馈，不能依赖任意多相机循环的自动排序。两张纹理的颜色存储约 `width * height * 16` 字节，另加 capture 的平台 depth/stencil 成本和更新时复制带宽；512² 的颜色部分约 4 MiB。这不是 memoryless／移动 RenderPass 优化。

专用 Camera 的 targetTexture、allowHDR、allowMSAA、renderingPath 在 finally 恢复；不覆盖其 view／projection／aspect，也不切全局质量或共享主材质。Camera 自己的 image effects／回调会照常执行，HDR 内容相机应避免曝光、tone mapping 或不受控的副作用。[Camera.Render](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera.Render.html)

## 支持边界与验证

当前限定 Built-in、固定完整视口、非 XR／动态分辨率、ARGBHalf 支持、1–4096 且不超过硬件限制的尺寸。源 Camera 必须 disabled，不能仅依赖模块帮忙停掉正在运行的相机。禁用组件立即释放；把 monitorEnabled 改为 false 时，应继续调用 TryUpdate 触发清理。RectMask2D 当前为硬边裁剪，没有软边 mask 支持。

已构建实际 UGUI 内容与发光网格的离屏 GPU 检查，覆盖 HDR 数值／UV 分区、alpha、材质与目标所有权、时间与错误输入。详见 [渲染记录](rendering.md)。视频编解码、原版完整舞台、移动设备／驱动、真实 GPU 帧时以及 Monitor 驱动的 decal lights 未据此验收；完整开项见 [技术清单](framework-techniques.md)。
