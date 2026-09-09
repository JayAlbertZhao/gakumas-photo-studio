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

首次配置、构建和运行步骤见[使用指南](../docs/user-guide.md)。可选构建期补充数据通过导入工具放入忽略的 Unity PrivateResources；详细格式见[资产接口](../docs/assets.md)。
