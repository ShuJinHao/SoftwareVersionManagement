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
    "bindings": [],
    "connections": [{
      "id": "<稳定连接 UUID>",
      "name": "<页面连接名称>",
      "baseUri": "https://<厂内 GitLab 主机>/",
      "enabled": true,
      "projects": [{ "id": 123, "name": "<项目名称>", "packages": ["<与流水线一致的包名>"] }]
    }]
  },
  "credentials": [],
  "connectionCredentials": [{
    "connectionId": "<同上连接 UUID>",
    "apiToken": "<只读 API token>",
    "packageToken": "<只读包 Deploy token>"
  }]
}
```

API token 使用 `read_api` 并只允许访问绑定项目，包 Deploy token 使用 `read_package_registry`。具体 GitLab 版本、账号角色及相关 API 可见性须实际验证。两个凭据分开保存：`PRIVATE-TOKEN` 只给固定项目元数据 REST 路径，`DEPLOY-TOKEN` 只给构造的 Generic 下载路径。拒绝 HTTP、重定向、任意 Release 下载地址和 URL 内嵌凭据；日志只含标识和错误代码。

正常使用操作系统可信证书链；企业私有 CA 可在顶层显式填写绝对 `trustCertificatePath`（只含公有根证书，不含私钥）。专用 CA 模式仍检查主机名、证书期限、链和服务端用途，但不联网查询吊销状态；证书轮换及撤销运营策略须在企业联调中落实，不能使用跳过证书验证的配置。

私有配置只提供可选连接、项目和包，不直接指定新软件绑定。每个连接最多 1000 项目，每项目最多 1000 包，总组合最多 10000。网页候选只返回这些允许项，搜索分页默认 50、最大 200。`release.source.manage` 为新的明确产品权限，需由管理员分配，不自动给已有人员或角色增加。

绑定在软件“版本与来源”保存到数据库。首次为 revision 0；后续启停、改绑必须携带当前 revision。允许更换连接、项目或包，但未结束扫描/导入/首次包处理必须先完成；返回 INVALID_STATE 及相关工作标识。来源变更新建稳定绑定及主体，旧记录不可覆盖，版本、同步工作、任务和履历仍引用原来源。纯启停保持当前绑定/主体，停止新增同步不取消已接受工作。部署移除连接后，需要远端的阶段等待配置恢复；已在本地派发的包工作和历史修复按版本自身来源继续执行，版本停用仍独立生效。新来源的数字版本与旧来源冲突时拒绝覆盖。

保存只是配置持久化，不代表连通或正式版已导入。后台实际核验保护 Tag、成功流水线和双副本后才展示正式版本。移除配置、停用或改绑均不自动恢复人工上传。

### 旧配置转换与显式接管

旧 `import.bindings`/`credentials` 文件仍可运行，数据库已有绑定不会被文件重写。转换工具先预览，不联系数据库或 GitLab，也不输出 token。使用绝对私有路径：

```sh
eng/node eng/gitlab/source-config.mjs preview /private/path/legacy-gitlab.json
eng/node eng/gitlab/source-config.mjs apply /private/path/legacy-gitlab.json /private/path/managed-gitlab.json
```

apply 只写一个不存在的新文件（Unix 0600），保留旧 bindings、credentials、绑定 ID、主体 ID 和启停状态，并为每项增加确定连接及允许项目/包。重复执行不会覆盖已有文件；发现已有同 ID 连接被人工改变时拒绝自动覆盖。审阅后由部署人员将各 Host 切换到新私有文件并重启。之后由具有 release.source.manage 的人员打开软件来源面板，选择相同项目/包、核对 revision 并保存，才显式接管到数据库管理；已有工作未结束时拒绝接管。相同来源接管保留原绑定和主体 ID。已接管软件不再被旧配置 enabled 覆盖；后续只在页面维护该软件。转换文件和原文件均含秘密，禁止提交仓库或公共 CI 产物。

第十四份迁移只在一次性库验证；生产/开发库升级仍需部署人员显式安排，工具不执行迁移。SQL 审阅产物为 artifacts/gitlab-source-management/upgrade.sql。

## 使用与恢复

首轮按有界页保存扫描进度和历史 Tag，仅导入最高的合格正式版；以后轮询发现新 Tag，不用版本高低作水位线，低版本维护分支的补丁也可导入。首轮其余历史版本标记 Historical，只有人员指定 Tag 才导入。流水线仍在执行的 Tag 不标记 Historical，下轮继续核验；即使首轮已导入其他合格版本，待完成 Tag 后来成功仍可自动发现，包括低版本维护补丁。全部待完成时保持首轮扫描，待合格后按最高版本规则选择。同步清单可多次核实，但同一软件/版本与绑定/Tag 唯一。

有 `software.read` 的人员查看来源和工作；有 `release.upload` 的人员可从版本页立即同步或指定历史 Tag。操作仍需人员首次改密完成、Cookie、HTTPS、CSRF、幂等键。同步返回 202 和工作地址；提交未知保留原键及正文，由人员核实。导入正式版不要求 `release.publish` 或本地测试结论。

断线、下载中断和 Worker 重启后，从持久化工作与租约代次接管；未完整接收的文件可在另一节点重新下载，已经核对的内容不覆盖。超过租约的旧执行器不能提交。如果已知 Tag 曾受旧版首轮扫描问题影响而被标记 Historical，由具有 release.upload 的人员指定该 Tag 同步恢复；平台不批量激活已有历史记录。已经进入错误队列的历史消息不会自动回灌；受控恢复须使用原 MessageId 和正文，不能伪造成功接收或换代次推进。清单/提交/内容变化产生冲突，不覆盖原发布事实；源端删除 Release 不删除本地历史或包。本地停用仍由有权人员执行。

下载接收、文件 SHA-256 与双副本就绪沿用平台包链路；双副本完成后直接 Formal，修复仅更改副本事实。导入不会改变实例安装状态，现场更新仍须人员明确创建任务，接入方领取、申请开始和实际安装后上报。

## 联调与依据

本仓测试使用真实 PostgreSQL、RabbitMQ、Nginx 和 HTTPS GitLab 协议夹具；实际 GitLab 尚未部署。待验证：GitLab 版本/API、企业证书、令牌角色/到期/轮换、保护 Tag 与重复上传策略、真实 Job/Registry 关联、实际安装包与厂家现场安装。相关局部结果见 [框架设计第 11 节](../../docs/软件框架设计.md#11-审阅出口与当前验证状态)。

官方契约：[Release API](https://docs.gitlab.com/api/releases/index)、[Tags API](https://docs.gitlab.com/api/tags/)、[Pipelines API](https://docs.gitlab.com/api/pipelines/)、[Jobs API](https://docs.gitlab.com/api/jobs/)、[Packages API](https://docs.gitlab.com/api/packages/)、[Generic Packages](https://docs.gitlab.com/user/packages/generic_packages/)。示例须在实际版本上 CI lint 后使用。
