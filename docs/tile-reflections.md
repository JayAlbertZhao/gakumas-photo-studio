# Tile 场景的 SSR 与 Probe 消费者

`SrpTileReflection` 是默认关闭的独立工具，供已有自有 SRP 消费 `TileSceneRenderer.PreparedFrame`。不启用 PhotoStudio 的新效果，不修改 Built-in 的 SSR／Planar／ReflectionResolve。当前包含真实场景 Hi-Z 射线追踪、历史深度拒绝、接收区约束的粗糙度过滤、Probe 回退、当前材质 Fresnel 响应与几何／映射法线差值扰动。

Planar 的 SRP 镜像相机／角色捕获尚未接入此类；本阶段不会将一张手工覆盖纹理计为真实 Planar。既有 Built-in Planar 保留，不能直接在 SRP 中调用其 `Camera.Render` 回调路径。

## 输入和调用顺序

Tile 设置需要 `geometryDepthId = true`，并提供以下已创建、独立、同尺寸的目标：

| 设置 | 格式 | 含义 |
| --- | --- | --- |
| `output` | B10G11R11_UFloatPack32 | 当前不含角色和间接镜面反射的场景 HDR |
| `normalIdentity` | R16G16B16A16_SFloat | 贴花后的映射世界法线与原有 group／GI 编码 |
| `materialBase` | R8G8B8A8_SRGB | 贴花后的基色与遮罩 R |
| `materialMos` | R8G8B8A8_UNorm | 贴花后的 metallic／AO／smoothness 与遮罩 GBA332 |

G0／G1 只是改为 Store，并未增加附件或 subpass，主颜色预算仍为 256 bits。两张新增导出图有 8 bytes/pixel 的名义保存量；不能据此声称没有带宽成本。frame 自有的 R32 eye depth／RGBA8 几何法线及资格来自此前的独立预处理。

持久创建 `new SrpTileReflection(camera, settings)`，显式设置 `enabled = true` 与 `sceneOnlyInput = true`。后者是宿主对输入内容的承诺：只注册场景表面，并在添加角色、透明对象或间接镜面光之前消费。排除层只关闭 SSR 资格，不能移除已经画入 HDR 的对象。

在宿主有效 SRP context、Tile pass 已结束的位置：

```csharp
context.SetupCameraProperties(camera);
if (!preparedScene.TryRecord(context, out _, out var error))
    throw new InvalidOperationException(error);
if (!reflections.TryRecord(context, preparedScene, sequence, sceneRevision,
        out var reflected, out error))
    throw new InvalidOperationException(error);
// reflected.color 是新 HDR 输出；宿主安排后续消费及 Submit。
```

`sequence` 从正数单调增加，一次记录需要一张新 Tile ticket。拓扑／内容不连续时修改 `sceneRevision`；序号跳跃、相机切换投影类型、大幅平移／旋转、尺寸或执行后端变化会冷启动。`ResetHistory()` 可显式清空历史。连续轻微相机运动使用前帧 view／projection 及深度一致性检查。

返回 frame 的 `IsCurrent` 表示当前记录的借用结果，**不是 GPU 完成信号**。调用者负责 Submit、GPU 同步以及输入／输出内容的生命周期；在上一批不再使用资源之前，不得再次记录、改写参数所依赖的纹理、调整尺寸或 Dispose。两次调用之间复用内部纹理，旧 ticket 失效。禁止借用结果当作下一帧 Tile 附件；需要跨帧保留结果时自行在 GPU 完成前安排独立复制。相机、Tile frame 被销毁或失效后，相应 ticket 也失效。

## 数据与画质约定

- Tile eye depth 背景值 0 先转换为 far clip，再逐层构造 ceil-half、边缘 clamp 的独立 R32 最小深度图，直到 1×1。当前层级构建使用 raster；`backend` 选择射线追踪和粗糙度过滤的 Raster／Compute，后者使用实际 RandomWrite 与 8×8 dispatch。`allowComputeFallback` 控制降级，实际结果见 `ActiveBackend`／`BackendFallbackReason`。
- SSR 使用未映射几何法线；资格为原始表面资格与贴花后 smoothness 阈值的交集。光滑贴花不能重新启用原本被排除的表面。过滤读取当前 smoothness、几何法线和深度；身份字段是 `receiverGroup + 1`，不是独立 Renderer ID。共用 group 的接收面还受法线、平面距离与 smoothness 差异约束。
- 只保存当前未合成的场景颜色和深度为历史。SSR 返回直通 HDR 辐射度与命中置信度，冷帧无 SSR；Probe 仍可响应。输出不会反馈进入下一次反射历史。
- 当前基色与 metallic 决定 `F0 = lerp(0.04, base, metallic)`；映射法线、视线和 smoothness／AO 决定独立 Schlick 响应。该近似沿用本工具的统一反射语义，不宣称原版方程或完整 split-sum IBL。
- 单一显式 Cubemap 支持 HDR 解码和受实际 mip 数限制的粗糙度 LOD。法线扰动使用 view-space `(mapped - geometric).xz * normalDistortion`，默认 `(0.1, -0.1)`；越界或跨 group 回退 Probe。Probe 本身不受屏幕偏移影响。未提供自动 Probe 选择／混合、原版 GGX 预滤波或多反射平面调度。

`Frame` 提供只读借用的 color、rawReflection、reflection、response、radiance、visibility，以及 `GetDepthLevel`。内部使用八张全尺寸 Half4、一张历史 R32 和完整 R32 层级；`NominalTextureBytes` 包含当前分配，即使暂未启用过滤也计入过滤图。预算不包含 Tile 源附件，亦不等于实际显存驻留、瞬时分配峰值或带宽测量。

## 验收范围

`--self-test-tile-reflection <目录>` 使用隔离自有 SRP、真实自制地板／双色墙和未注册角色。逐像素检查 Store 后材质、零深度转换与完整最小层级、Fresnel 响应、Probe／SSR 扰动选择与最终合成；另有实际反射命中、Raster／Compute、两轴过滤、非递归历史、贴花、冷启动及生命周期控制。脚本导出 PNG 和 float RAW；原版资产不参与该测试。

桌面 D3D11／Vulkan 各执行 365 条具名条件、24 组启用配置及关闭控制，每次输出 456 张预览及对应 RAW。50 份原生捕获核对 G0／G1 Store、真实 Draw／Dispatch、当前生产者资源与字节、全部层级及反射字段、历史来源和独立材质／合成方程。粗糙度两轴过滤另有全图 CPU 世界空间射线对照。共享追踪算法的实景命中使用双色墙内部解析检查，不将其扩大为每条 miss 射线的形式化证明。

原生 G0 保存的是 sRGB8 码点。引擎 Blit／ReadPixels 的浮点转换与标准 EOTF 在当前配置最大相差约 0.001679；所有像素重新编码后与原始码点一致。原生材质方程的独立检查使用原始字节的标准 EOTF，不以放宽后的浮点图替代实际输入。其他 Half4／R32 输出与相应原生字段逐位一致；MOS／几何 UNorm8 的读回只有浮点表示误差。

桌面测试不能替代整套应用集成：SRP Planar／Actor／运动附件与原生生产场景仍需继续接入，移动、Metal、RenderPass 带宽收益和完整讲演画质尚未验收。现有 Direct 诊断模式退出警告也没有因此关闭。
