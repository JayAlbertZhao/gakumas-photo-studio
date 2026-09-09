# 本地资产接口

集中入口为根目录 `LocalAssets/`；只提交其中 README。既有私人原件继续保存在仓库外。

```text
LocalAssets/
  runtime/
    staging-manifest.json
    bundles/
    voices/
    story-timeline.json       # 可选，转换后的时间线
    face-motions.json         # 可选，面部曲线
    photo-facial-motions.json # 可选
    face-decals.json          # 可选
  resources/                 # 可选，环境/可读网格
  reference/                 # 私人参考，不发布
```

启动前设置 `GAKUMAS_PHOTO_STAGING` 为自己的 runtime 绝对路径，必须含 `staging-manifest.json`。河岸场景可选数据使用 `GAKUMAS_RIVERBED_STAGING`。

推荐通过 `python -I studio.py configure --data "<数据目录>"` 保存本地路径，再运行 `python -I studio.py doctor`。外围启动器替你设置上述既有环境变量，不移动资产、不改 Unity 默认值。路径优先级为命令行选项 → studio.local.json → 已有环境变量 → 仓库的 LocalAssets/runtime。配置中的相对路径相对于仓库；命令行相对路径相对于当前终端目录。

可选编辑器环境变量为 `TUANJIE_EDITOR`。临时检查其他数据集可用 `python -I studio.py doctor --data "<另一目录>"`，不会覆盖本地配置。`config/studio.example.json` 是本地配置结构示例，不存储密码、密钥或账户。

为保持 C# 原文，本版保留两处不含用户名或凭据的旧项目默认路径；用户通过上述已有环境变量覆盖。不读取 `asset-paths.local.json`、`render-profile.json`，也不支持 `--data`。

清单结构见 `config/staging-manifest.example.json`；它是空格式示例，不是可运行数据。BundleRecord 的现有字段为 `name`、`role`、`label`、`requested`、`output_relative_path`、`dependencies`。角色部件主要由资源命名约定识别，没有新工具层的通用角色/变体元数据解析。

### 什么叫“准备好的兼容数据集”

需要当前引擎能够加载的 AssetBundle，角色部件 prefab 具有匹配的骨架/蒙皮、材质与依赖，身体和面部动作具有对应动画类型。目录中每个清单路径都要指向实际文件。重命名一个 FBX 或原始游戏缓存文件并不能转换它。

脸部资源 ID 的现有约定是 `mdl_chr_<角色ID>-<变体>_face`；身体与头发使用同角色 ID 的相应部件。至少需要 face、costume、hair、motion 四类记录。`python -I studio.py catalog` 列出数据集识别到的角色和资源 ID；非默认角色通过 `photo --character <ID>` 指定。

仓库不提供从官方游戏缓存到该清单的通用提取流程，也不验证输入的使用权。没有已准备兼容数据的使用者，目前无法仅靠此仓库完成端到端动画制作。doctor 的结构检查不能代替 Unity 内的资产内容和画面验证。

主要 role 为 `face`、`costume`、`hair`、`motion`、`dependency`。语音记录使用 `label` 和 `output_relative_path`。需要匹配的面部、身体、头发、动作和依赖，不支持任意缓存、FBX 或动画文件即插即用。程序预加载清单中的 bundle，缺失或不兼容会失败。仅使用可信清单；旧路径解析不是安全沙箱。

## 环境和网格补充

```powershell
python -I tools/import_private_resources.py --source ./LocalAssets/resources
```

导入器仅接受约定环境数据和 `ReadableBodyMeshes` 下的网格，目标为忽略的 `unity/Assets/PrivateResources/Resources`。缺失补充数据可能改变反射、网格读取和换装结果；没有素材的构建通过不代表视觉一致。

LUT 仍按 `OriginalStyleRenderPipeline.EnsureCapturedColorLut` 中的历史相对路径加载，本次保留代码。对应环境、网格及捕获 LUT 均不提供。

保护包若需要密钥，由使用者通过已有的 `GAKUMAS_UNITYCN_KEY` 提供。仓库不提供密钥、下载器、解包器或注入器。
