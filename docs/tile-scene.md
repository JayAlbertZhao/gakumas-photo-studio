# 显式 Tile 场景材质与光照

`TileSceneRenderer` 是默认关闭、供自有 SRP 调用的场景消费者。输入复用 `SceneDeferredCamera.Surface`、`MaterialInputs`、`SceneGiInput` 和 `SceneBakedShadowInput`；不会切换摄影应用的 Built-in 管线，不修改已有 Deferred GBuffer ABI。

当前接入普通不透明／cutout 表面、线性 RGB 切线法线、UV0 材质、UV2／Probe GI、四通道烘焙可见性及显式方向光 PBR／GI 乘色／背向漫反射。另有显式位置光照路径，以及 [几何法线／SSR 资格与原位材质贴花](tile-decals.md)。SSR／Probe 已有显式 [当前帧消费者](tile-reflections.md)；薄叶、Planar、Actor 和运动附件尚未自动整合；薄叶输入明确拒绝。源讲演只提供技术布局，这些材质、编码和光照方程是本工具包的独立实现。

## 调用与所有权

创建 `TileSceneRenderer.Settings`，显式开启，指定表面及已创建的 `B10G11R11_UFloatPack32` 输出；可选提供 `R16G16B16A16_SFloat` 法线目标，以及 `materialBase`（sRGB8）／`materialMos`（UNorm8）后续消费者导出。固定单层 2D、完整视口、非 MSAA／mipmap／dynamic scale／random write，Linear 项目。输出不能与任何材质、GI 或烘焙遮罩输入别名。

`TryPrepare(camera, settings, out frame, out error)` 不绘制、不分配输出、不修改 Camera；生成拥有独立材质的单次快照。宿主在有效 SRP context 中完成 Camera 设置后调用 `frame.TryRecord`，随后自行 `context.Submit`。材质参数已快照，网格、Renderer 当前几何、纹理和目标仍借用，必须保持有效且不被并发修改。等 GPU 不再使用本批资源后才 `frame.Dispose()`；提交返回或 `Budget` 均不代表 GPU 完成。快照不能重复记录；关闭、非法输入、能力不足返回具体错误，不偷偷回退默认相机。

当前消费者限定桌面 Vulkan 与显式允许的 D3D11 仿真。默认方向光 PBR 只需要相机射线，使用有限 far-plane 端点，不读取深度附件；这不提供实际表面世界位置。Metal／移动端仍待验收。

## 显式位置光照

设置 `positionLighting = true` 后，准备独立的当前几何深度预处理与位置光照。`localLights` 复用 Point／Capsule／Area／Spot 参数和源阴影配置，`mainLightShadow` 复用方向光阴影配置。`localLightBackend` 显式选择已有的 GPU 网格或 BruteForce；`allowLightFallback = false` 可禁止网格能力／预算不足时降级。该路径采用单次全屏求值、FP32 累加局部灯后量化，未宣称与讲演的实例化灯体绘制相同。`localLights.backend` 的 Scalar／Instanced 选择仅适用于既有 Deferred 消费者，不控制此处的网格。

预处理使用同批 Renderer／submesh、当前蒙皮、顶点缩放、VP、UV0 cutout 与剔除配置，输出独立 `R32_SFloat` 正 eye depth，并有自己的 D32 遮挡测试。预处理完成后，主光照通过普通纹理精确像素读取重建世界位置；不会采样主 render pass 当前绑定的 D32。`frame.EyeDepth` 属于 frame，调用者只能在记录完成及必要的 GPU 同步后读取，不能释放或写入；frame 释放后失效。

`Budget` 仍描述主 pass；`DepthBudget` 单独描述预处理，增加 32 color bits／32 depth bits、8 bytes/pixel，其中 4 bytes/pixel 深度色图需要保存。因此两组附件名义合计 36 bytes/pixel，受同一个 `maximumAttachmentMiB` 合并检查。灯表／网格的 `LightBufferBytes` 和阴影图有各自预算，不能用主 pass 的 256-bit 数字表示总资源成本。`LocalLightCount`、`LocalLightBackend`、`ShadowMapCount` 供宿主检查实际提交。

材质、GI、烘焙遮罩继续来自主五 MRT。按像素解码 receiverGroup；烘焙和实时可见性取较小值。所有预处理、阴影与主 pass 记录后仍由调用者统一 Submit、负责 GPU 生命周期。开始记录辅助命令后的失败会消耗该单次 frame，不能重试半批工作。输入 `positionLighting = false` 却请求局部灯／实时阴影会明确拒绝，不静默丢灯。

`localLights.atlas` 可借用显式 HDR 2D 纹理。动态内容可显式绑定 `localLights.srpMonitor`，使用 [SrpHdrMonitor 的 prepare-before-SRP／record](hdr-monitor.md)；现有 `HdrMonitor` 仍要求 Built-in，不直接用于这个 SRP 路径。已有真实 UGUI → HDR 发布 → 发光网格／Point／Capsule／Area 与网格灯的对照，完整舞台与视频输入仍需单独验收。

## 五 MRT 与精度

| 附件 | 格式与内容 | 后续用途 |
| --- | --- | --- |
| G0 | sRGB8 RGB 基色、A 遮罩 R8 | 光照当前像素输入，默认 transient、可选 Store |
| G1 | UNorm8 MOS、A 遮罩 GBA332 | 光照当前像素输入，默认 transient、可选 Store |
| G2 | Half4 世界法线、A 自主身份标记 | 可选保存，不写运动矢量 |
| G3 | packed HDR 自发光，随后加方向／间接光 | 最终 resolve 输入，transient |
| G4 | packed HDR GI | GI 消费后复用为最终输出 |
| 深度 | D32 | 几何遮挡，后续 subpass 保持只读，不作为光照输入 |

G2 A 的 0 表示背景；有几何时为 `1 + receiverGroup + (hasGi ? 256 : 0)`，最大 512 可被 Half 精确表达。此编码不是原版 MaterialID。R8／GBA332 使用现有独立遮罩编码与可选 dither，不更改旧 Deferred 的额外 RG8 附件。

颜色保守预算 256 bits，深度另计 32 bits；物理格式合计 28 bytes/pixel。数值是声明预算，不是实测驻留／带宽。sRGB8、UNorm8 和 packed HDR 的多次格式量化不等于旧 Half4 输出逐位相同；最亮 HDR 最终显式截到 R/G 65024、B 64512，背景与几何的 alpha 均为 1。G3 加法可溢出，最终 resolve 负责有限上限；不是透明合成附件。

## 桌面验证与剩余范围

`--self-test-tile-scene <输出目录>` 在隔离的自有 SRP 中执行 28 组自制材质配置：金属／介电、sRGB／MOS 中点量化、cutout、发光、法线／镜像／非均匀缩放、透视观察射线、UV0／UV2 分离、Lightmap／SH、GI 乘色、AO、四个遮罩通道、背光、HDR 上限、重叠接收面和实际当前单骨蒙皮、奇数尺寸。完整颜色与法线字段逐像素验收，非法输入、别名、单次记录和借用资源另有检查。

可设置 `GAKUMAS_SELFTEST_TILE_SCENE_LEGACY=1` 运行同一材质配置的既有 `SceneDeferredCamera` 消费者；它是独立控制模式，不是自动运行时降级。两种输出分别按对应格式和同一光照语义对照。Point 采样的奇数尺寸配置将输入域避开精确 texel 边界，避免把插值浮点舍入的相邻 texel 选择误判为确定的单一结果；sRGB／UNorm 中点则枚举事先规定的相邻格式值。

D3D11／Vulkan 的 56 份原生捕获检查了五 MRT、独立解码材质、几何深度、四个同像素光照输入、加法混合、G4 复用及真实蒙皮顶点变化。D3D11 捕获使用显式 `-force-gfx-direct`，并与正常线程输出分别核对；该诊断模式仍会出现四条退出时的 bound-color-surface 释放警告，不启用 RenderDoc 时也存在，未定位根因、不推荐作为生产模式。正常线程 D3D11／Vulkan 本模块运行没有这些警告。

`--self-test-tile-position <输出目录>` 单独运行 54 组位置配置：透视／正交、倾斜表面的近／远深度、四种局部灯、网格／暴力对照、receiverGroup、HDR atlas、GI 乘色、110 灯累加、四种灯的当前遮挡与遮挡面移到接收面后方、方向阴影、强度与 baked/current min、缩放 cutout、移动透视相机。每组保存颜色、真实 eye depth、法线，逐像素检查独立射线／平面深度与灯光方程，同时检查附件预算、实际 backend／阴影图数和单次记录。

数值验收先独立验证深度与法线，再使用已验证的存储值对照光照；CPU 相机射线不复用着色器的 inverse-VP 重建。深度绝对误差预算为 `2e-4`，法线在 `1e-6` 浮点计算预算内枚举相邻 Half 值；光照的 `2e-5 * max(1, abs(value))` 运算预算先传递到 packed HDR 转换，再检查离散格式值。可见性导致的精确零仍要求零。不删除量化边界像素，也不把这些工程验收预算当作形式化误差证明。

本机编辑器版本的 CPU `ReadPixels` 对 packed HDR 正 subnormal 的解码与原生 GPU 字节不一致，因此位置测试先在 GPU 转为 RGBAFloat 再读回；原生捕获另行解码验证。该修正仅用于新测试的读回，不更改默认摄影或 GPU 光照逻辑。

位置路径另有 108 份 D3D11／Vulkan 原生捕获：逐组核对真实 R32 生产者与光照输入的资源身份、整图深度／材质／GI／法线／位置光照、当前灯表与实际阴影绘制、五 MRT 和 G4 复用。Vulkan 检查了独立深度 pass 保存 R32 后再开始主 pass 的实际 load/store 与 subpass 布局。GPU Float 读回与原生 packed HDR 解码逐位相同。Direct 模式仍有上述四条释放警告；正常线程运行无新增释放警告。

上述证据限于自制桌面内容，尚不证明完整角色／场景或所有 GI 导入变体、移动收益及长时间资源行为。材质贴花与实时 Monitor 的新增范围分别见 [贴花接入](tile-decals.md) 和 [Monitor](hdr-monitor.md)。Planar、Actor、运动的完整 tile 整合，以及位置／贴花的蒙皮与复杂遮挡组合仍需继续完成，未宣布框架全部追平。默认摄影路径保持原样。
