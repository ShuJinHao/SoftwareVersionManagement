# GitLab 正式发布接入

本目录是可复制的接入工具和示例，不部署 GitLab，也不修改上位机、视觉软件的流水线。平台只接受已绑定项目的正式受保护 Tag。Issues、develop/release/master 合并策略及整体测试在 GitLab 管理；平台不检查 Issue 关闭状态、不替代研发审批，也不在导入后自动投放设备。

## 发布端

1. 在受管软件仓库按团队规则完成测试、合并和保护正式 Tag；使用 `X.Y.Z` 或 `vX.Y.Z`，三段规范整数，首段至少 1，不带预发布后缀。两种写法归一为同一数字版本，不能重复占用。
2. 复制 [manifest.mjs](manifest.mjs) 到该仓 `ci/manifest.mjs`。按照 [CI 示例](gitlab-ci.example.yml) 替换包名、文件和构建/测试脚本，使用企业批准的 Runner 工具及证书。示例没有给生产构建镜像或依赖版本作选择。
3. 同一受保护 Tag 流水线的产物 Job 构建最终安装包，生成清单，并用 `CI_JOB_TOKEN` 上传包和清单到同项目 Generic Package Registry。包版本必须保留原始 Tag；安装文件为安全单段文件名，清单名固定 `release-manifest.json`。Release 的两项 `assets.links[].url` 必须是这两个 REST v4 Generic 下载地址。
4. 关闭 Generic 同名文件重复上传，并限制谁能创建、删除或重建保护 Tag/Release。不得覆盖已发布 Tag 来修正构建；生成新的版本。即使源端允许重复，平台也会拒绝同包重复文件或不一致的摘要。流水线尚未成功时平台保留等待，不提前下载供包。

发布清单 V1 字段：`schemaVersion=1`、`projectId`、`tag`、规范化 `version`、`commitSha`、`pipelineId`、产物 `jobId`、`packageName`、`packageVersion`、`fileName`、`sizeBytes`、`sha256`。工具通过 CI 环境变量取得构建身份，按 1 MiB 流读取文件计算摘要，拒绝覆盖已有输出；输入文件生成后保持不变。源端发布说明目前映射 Release 名称，详细研发说明保留在 GitLab 链接中。

平台核对 Release、保护 Tag、成功 Pipeline/Job 的项目、提交和 Tag，另外检查 Registry 文件大小、SHA-256、唯一文件及同流水线关联；随后独立下载复核字节摘要。清单不是数字签名，信任边界仍包括 GitLab 管理员、保护规则及 Runner；实际企业流水线须联调确认这些策略。

## 平台私有配置

HttpApi 与两个 Worker 使用一致的 `SVM_GITLAB_CONFIG_FILE`，并已配置本项目厂区、数据库、包节点及 RabbitMQ。路径须绝对，JSON 文件仅运行账号可读（Unix 0600）；下例全部尖括号项须由部署人员填写，没有默认软件或设备：

```json
{
  "import": {
    "pollSeconds": 300,
    "pageSize": 20,
    "maxPages": 10000,
    "leaseSeconds": 120,
    "bindings": [{
      "id": "<稳定绑定 UUID>",
      "softwareId": "<平台已有软件 UUID>",
      "subjectId": "<本绑定专用集成主体 UUID>",
      "baseUri": "https://<厂内 GitLab 主机>/",
      "projectId": 123,
      "packageName": "<与流水线一致的包名>",
      "enabled": true
    }]
  },
  "credentials": [{
    "bindingId": "<同上绑定 UUID>",
    "apiToken": "<只读 API token>",
    "packageToken": "<只读 Deploy token>"
  }]
}
```

API token 使用 `read_api` 并只允许访问绑定项目，包 Deploy token 使用 `read_package_registry`。具体 GitLab 版本、账号角色及相关 API 可见性须实际验证。两个凭据分开保存：`PRIVATE-TOKEN` 只给固定项目元数据 REST 路径，`DEPLOY-TOKEN` 只给构造的 Generic 下载路径。拒绝 HTTP、重定向、任意 Release 下载地址和 URL 内嵌凭据；日志只含标识和错误代码。

正常使用操作系统可信证书链；企业私有 CA 可在顶层显式填写绝对 `trustCertificatePath`（只含公有根证书，不含私钥）。专用 CA 模式仍检查主机名、证书期限、链和服务端用途，但不联网查询吊销状态；证书轮换及撤销运营策略须在企业联调中落实，不能使用跳过证书验证的配置。

绑定首次写入后固定项目、地址、包名、主体及软件关系，不允许通过换配置覆盖历史；停用可将 `enabled` 设为 false，轮换凭据不改变绑定标识。同步停用或移除配置会阻止新扫描和导入，已派发的包工作继续依据持久化绑定及不可变来源核验并形成双副本；停止某个版本的执行仍须通过平台版本停用操作。没有配置时未绑定软件沿用人工方式；已持久化绑定的软件不因移除配置退回人工创建。无效配置拒绝启动。

## 使用与恢复

首轮按有界页保存扫描进度和历史 Tag，仅导入最高的合格正式版；以后轮询发现新 Tag，不用版本高低作水位线，低版本维护分支的补丁也可导入。首轮其余历史版本标记 Historical，只有人员指定 Tag 才导入。流水线仍在执行的 Tag 不标记 Historical，下轮继续核验；即使首轮已导入其他合格版本，待完成 Tag 后来成功仍可自动发现，包括低版本维护补丁。全部待完成时保持首轮扫描，待合格后按最高版本规则选择。同步清单可多次核实，但同一软件/版本与绑定/Tag 唯一。

有 `software.read` 的人员查看来源和工作；有 `release.upload` 的人员可从版本页立即同步或指定历史 Tag。操作仍需人员首次改密完成、Cookie、HTTPS、CSRF、幂等键。同步返回 202 和工作地址；提交未知保留原键及正文，由人员核实。导入正式版不要求 `release.publish` 或本地测试结论。

断线、下载中断和 Worker 重启后，从持久化工作与租约代次接管；未完整接收的文件可在另一节点重新下载，已经核对的内容不覆盖。超过租约的旧执行器不能提交。如果已知 Tag 曾受旧版首轮扫描问题影响而被标记 Historical，由具有 release.upload 的人员指定该 Tag 同步恢复；平台不批量激活已有历史记录。已经进入错误队列的历史消息不会自动回灌；受控恢复须使用原 MessageId 和正文，不能伪造成功接收或换代次推进。清单/提交/内容变化产生冲突，不覆盖原发布事实；源端删除 Release 不删除本地历史或包。本地停用仍由有权人员执行。

下载接收、文件 SHA-256 与双副本就绪沿用平台包链路；双副本完成后直接 Formal，修复仅更改副本事实。导入不会改变实例安装状态，现场更新仍须人员明确创建任务，接入方领取、申请开始和实际安装后上报。

## 联调与依据

本仓测试使用真实 PostgreSQL、RabbitMQ、Nginx 和 HTTPS GitLab 协议夹具；实际 GitLab 尚未部署。待验证：GitLab 版本/API、企业证书、令牌角色/到期/轮换、保护 Tag 与重复上传策略、真实 Job/Registry 关联、实际安装包与厂家现场安装。相关局部结果见 [框架设计第 11 节](../../docs/软件框架设计.md#11-审阅出口与当前验证状态)。

官方契约：[Release API](https://docs.gitlab.com/api/releases/index)、[Tags API](https://docs.gitlab.com/api/tags/)、[Pipelines API](https://docs.gitlab.com/api/pipelines/)、[Jobs API](https://docs.gitlab.com/api/jobs/)、[Packages API](https://docs.gitlab.com/api/packages/)、[Generic Packages](https://docs.gitlab.com/user/packages/generic_packages/)。示例须在实际版本上 CI lint 后使用。
