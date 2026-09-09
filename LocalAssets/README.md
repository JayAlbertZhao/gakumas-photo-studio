# 本地资产入口（内容不提交）

此目录只提交本说明。将自行准备且有权使用的内容集中在这里：

推荐用 `python -I studio.py configure --data "<数据目录>"` 配置，`python -I studio.py doctor` 检查；也允许数据留在仓库外，不要求复制进本目录。

```text
LocalAssets/
  runtime/       # staging-manifest.json、bundles、voices、story-timeline.json
  resources/     # 本地可选环境数据及可读网格，供导入工具使用
  reference/     # 私人研究资料，永不发布
```

当前运行时保留了旧工程的默认路径。启动前必须将 `GAKUMAS_PHOTO_STAGING` 设为自己的 `runtime` 绝对路径；不要求创建与开发者相同的磁盘目录。可选河岸数据使用已有的 `GAKUMAS_RIVERBED_STAGING`。详见 [资产接口](../docs/assets.md)。

`.gitignore` 默认拒绝所有未知文件；`public-files.json` 只允许本 README。不要强制添加资产。不要把本地构建、打包整个工作目录或父仓库历史作为发行物。

## 私人工作包迁移

若已收到自己的私人资产 ZIP，请解压到仓库根目录，与 `studio.py` 同级，**不要解压到本目录内部**。只使用新的克隆，避免覆盖已有剧情和本机配置。随后执行 `python -I tools/verify_local_assets.py`，按 [换电脑使用指南](../docs/user-guide.md) 配置新电脑的编辑器并构建。

模型和动作放在本目录的 runtime，构建期补充数据另在忽略的 Unity PrivateResources。这里只公开本 README；PACKAGE.json、START-HERE.md、runtime、截图和自己的脚本均不提交。
