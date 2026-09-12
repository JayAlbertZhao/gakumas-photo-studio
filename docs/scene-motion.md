# 场景运动对应数据

`SceneDeferredCamera.motion` 提供显式场景表面上次渲染到本次渲染的顶点对应，作为 PDF17／33 运动附件及 PPT126 时域效果的数据基础。运动模块本身不执行历史颜色／AO 累积；另有显式选择的 [颜色 TAA](scene-temporal-antialiasing.md) 与 [时域 GTAO](scene-gtao-temporal.md) 消费模块。不自动接到旧 TAA、SSR 或 Motion Blur。

```csharp
scene.motion.enabled = true; // 默认关闭，不修改现有 Photo Studio
scene.motion.maximumTrackedVertices = 1000000;
scene.motion.cameraCutDistance = 1; // 世界单位
scene.motion.cameraCutAngle = 30;  // 度
camera.Render();
if (scene.TryGetFrame(out var frame))
{
    var motion = frame.motionVectors;
    var previousGeometry = frame.previousNormalIdentity;
}
// seek、场景替换或应用定义的切镜：
scene.ResetMotionHistory();
```

仍须按 [场景后端](scene-deferred.md) 注册 `surfaces` 并由宿主排除其层。不依赖宿主 `_CameraMotionVectorsTexture`：显式场景层不在普通相机的剔除集合中。

## 数据契约

| 附件 | 通道 | 含义 |
| --- | --- | --- |
| `motionVectors` | RG | 当前纹理 UV − 上次纹理 UV；`previousUv = currentUv - motion.xy` |
| 同上 | B | 当前片元对应位置在上次视图中的正视深度，世界单位 |
| 同上 | A | 1 表示有几何对应，0 表示不可用 |
| `previousNormalIdentity` | RGB | 对应的上次世界网格法线，插值后归一化；不读 normal map 或贴花法线 |
| 同上 | A | 当前表面的相机私有整数身份；背景为 0，不是原版 MaterialID 或 TAA flags |

UV 遵循模块 shader 的纹理坐标；CPU 读回不应仅凭 `graphicsUVStartsAtTop` 再翻转。无对应时 motion、法线全零，当前可见表面仍写身份。身份按 `(Renderer, materialIndex)` 分配，持续登记时不受数组重排影响；释放后重登分配新身份，整个模块释放后不保证编号延续。

对应有效不表示上次该像素可见。画外位置仍可能有对应；消费方必须检查 UV 边界、上次实际深度／法线／身份、遮挡与反遮挡，以及光照／遮挡物变化，不能仅凭 A=1 累积历史。

“上次”是本相机上次成功的 `Camera.Render`，允许同一游戏帧多次或按需隔帧渲染。颜色／AO 消费方须管理匹配历史的时间和渲染序号，长暂停或 seek 应显式 reset。保留完整上次投影矩阵，FOV、正交切换、偏移投影不会被相机位移近似替代。

## 实际 GPU 快照

每个表面增加一次 `DrawRenderer`，geometry shader 把每个三角形的实际顶点写入私有纹理对应整数槽，保存世界位置／法线。重复顶点写同样数据，未索引槽清零。随后再次绘制当前几何，以 `SV_VertexID` 读取上一快照生成运动 MRT。只在成功渲染后交换快照，无 CPU 顶点读回、`BakeMesh` 或重算蒙皮。

使用与场景绘制一致的引擎顶点流及 `unity_ObjectToWorld`，包括骨骼、blendshape、根骨矩阵、非均匀缩放和 `vertexScale`。CPU `BakeMesh` 的坐标、缩放及更新时间可能与 `DrawRenderer` 不同，仅乘 `Renderer.localToWorldMatrix` 不足以证明一致。自定义 shader 位移不在现有场景契约中，不自动捕获任意材质的顶点程序。

首次、reset、相机位移超过 distance 或 forward／up 轴变化超过 angle 时拒绝历史。尺寸变化、目标丢失、网格／索引拓扑／顶点数量、`motionRevision`、alpha 纹理及 CPU revision、UV 变换、alpha／cutoff／cull 改变，也使对应历史失效。带 cutout 的 RenderTexture alpha 缺少可靠 CPU 更新标记，每次均拒绝历史。重新分配顶点身份、修改 UV 或 GPU 改写 alpha 未更新纹理 revision 时，调用方应递增 `Surface.motionRevision`。

隐藏、禁用或移除的表面在完成渲染后释放自身快照，恢复从无历史开始。双相机不共享；禁用／无效输入释放全部私有资源。Frame 附件仅借用，不得写入、Release 或跨渲染保留使用权；reset 立即使 Frame 失效，开关变更须等待下一次相机渲染才能查询新输出。

## 成本与边界

- 两张额外全分辨率 RGBAFloat 共 `32 × width × height` 字节，另有一份请求 24 位的深度附件；不改旧 GBuffer／最终 HDR。
- 每表面两张额外 RGBAFloat 顶点快照，每顶点每张两个 float4。令 `N = vertexCount`，`W = min(nextPowerOfTwo(ceil(sqrt(2N))), 1024, maxTextureSize)`，`H = ceil(2N/W)`，两张共 `32WH` 字节；不同子网格单独预算，未索引顶点仍占槽。
- 每活跃表面增加一次快照 draw 和一次运动 draw。`MotionTargetCount` 统计全分辨率两张；`MotionSnapshotTargetCount` 统计所有顶点快照；`MotionDrawCalls`、`MotionSnapshotDrawCalls` 分别统计绘制。
- 默认关闭不分配、不绘制。启用需 geometry shader、RGBAFloat Render／Sample 及场景后端既有条件，缺失能力拒绝场景帧，不伪造相机运动。
- 为逐次核验索引对应，网格须 CPU-readable 且为三角形；静态合批 renderer 拒绝，避免合并顶点流 ID 与局部网格不一致。顶点预算为 1–4000000，distance 为 0–100000，angle 为 0–180，均须有限。

已验证 t15／D3D11 桌面隐藏 Player 的真实离屏路径；[GTAO 时域模块](scene-gtao-temporal.md) 和 [场景颜色 TAA](scene-temporal-antialiasing.md) 已可显式消费对应数据。geometry shader 后端不构成移动优化；Metal、Vulkan、移动设备、Memoryless 或 GPU 帧时均未验收。便携高效后端、Motion Blur 消费、完整角色分类和完整场景动态画质仍待后续阶段。执行记录见 [渲染记录](rendering.md)。
