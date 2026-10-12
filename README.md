# SoftwareVersionManagement

面向隔离厂区的制造业软件版本管控平台。目标交付为 Linux 上的 .NET 8 服务端、网页管理端、管理 API、接入 API 和厂家接入文档。

现场入口按“厂区 → 工序 → 设备 → 对应软件”组织；本平台独立维护台账及强制设备映射。上位机、视觉等可执行软件管理版本、EXE 或安装包、投放任务和上报事实；接入方通过 API 获取任务，负责本机安装、回退及数据库和日志保护。PLC 仅记录后续程序备份、压缩包归档方向，具体流程尚待确认。业务规则以[需求规格说明](docs/需求规格说明.md)为准。

## 当前状态

项目处于基础框架和管理功能开发阶段，尚未完成系统验收或生产部署。已有人员管理、现场台账、上位机/视觉软件目录、人员软件授权、设备映射，以及实例登记/恢复、独立凭据和状态上报 API 及同源网页；进程内领域事件、PostgreSQL 工作单元和 RabbitMQ 发送/消费恢复基础保留。设备软件按真实接入事实区分“尚未登记”“尚未上报”和报告新鲜度，展示实际版本、IP、运行及接入状态；版本库、安装包和任务尚未实现。

| 已有实现 | 后续实现 |
|---|---|
| 24 个 C# 项目、82 条普通项目引用、固定工具链和依赖锁、编译期架构检查 | 完整系统、Linux 部署与生产验收 |
| DDD 基础类型、IOC、CQRS 请求分类/验证/授权基础 | 管理系统凭据与正式工作身份 |
| PostgreSQL 工作单元、只读连接、显式迁移与人员播种 | 版本、安装包、批量任务和部分回退 |
| 人员登录、首次/本人改密、退出、共享会话及必要审计 | 外部管理系统的凭据接入 |
| 人员管理、四项厂级及软件范围授权、软件目录、工序/设备/映射维护与现场导航 | 软件版本、测试转正式与投放 |
| 受限登记许可、单次恢复、独立实例凭据吊销、报告流/快照、安装履历及接入管理网页 | 平台版本关联及测试证据、任务与厂家现场验收 |
| 所属模块的类型化领域事件处理器、Scoped 串行派发、提交成功后的内部确认 | 实际业务事件及领域事实到版本化集成消息的转换 |
| 持久化幂等协调器、六模块操作结果存储、人员管理 HTTP 幂等及提交结果核实 | 其他业务 HTTP 幂等接入及完整事务/消息验收 |
| 三类固定 V1 消息、MassTransit EF Bus Outbox、Worker 投递及 RabbitMQ 断线/重启恢复 | 实际业务生产者、消费者与持续业务去重 |
| 显式消费目录、原生 Consumer Outbox/Inbox 事务、当前授权先于去重、有界重试及崩溃恢复 | 所属模块的正式工作事实、长工作执行与消费观测 |
| 密码哈希、随机凭据校验及密钥保护证书加载 | 文件副本与清理、完整日志/观测/健康检查接入 |

会话接口为 `GET/POST/DELETE /api/v1/session` 和 `POST /api/v1/session/password`。新增 `GET/POST /api/v1/manage/users`、`GET/PATCH /api/v1/manage/users/{userId}`、`POST /api/v1/manage/users/{userId}/reset-password`、`PUT /api/v1/manage/subjects/{subjectId}/permissions`，均仅允许已首次改密且当前具有 `identity.manage` 的人员。

工号唯一且不可修改，无删除历史主体接口；新建/重置要求下次改密，停用/重置同事务撤销旧会话。IAM 事务锁与主体保护保证并发操作后至少保留一名启用且持有厂级 `identity.manage` 的人员。管理员可编辑四项厂级权限及现有目录中的软件范围操作；新增软件授权必须引用真实软件。软件创建者只同事务取得 `software.read`、`instance.read`、`instance.manage`。其他业务 Command 继续禁用；发送端尚无实际业务生产者，Worker 未注册业务消费者。

现场接口为 `/api/v1/manage/site`、`processes`、`devices`、`software` 及设备下的 `software-bindings`、`software-inventory`；另有 `permission-options` 为人员管理员提供有界授权候选。台账查看/维护分别要求 `asset.read`/`asset.manage`；映射维护另需对应软件 `instance.manage`，资料修改需 `release.upload`。软件清单按 software.read 过滤，设备软件汇总另需 instance.read；数量只描述过滤后的本页对象。代码、设备编号和软件分类不可修改，映射逻辑撤销保留标识，重建继续 revision；原键重放仅核实，不再次改变关联。真实登记与映射引用确认同事务，已引用映射不能撤销；登记与撤销并发由数据库保护裁决。

接入管理路径为 `/api/v1/manage/enrollment-grants`、`instances`、实例下的 `credentials`、`recovery-grants`、`version-history`、`lifecycle`，以及凭据/许可的撤销路径；签发、查询和撤销凭据或许可需软件 enrollment.manage，仅人员，创建软件不会自动取得该权限。厂家使用 `/api/v1/enrollment/instances`、`recoveries` 登记及恢复，独立实例 Bearer 调用 `/api/v1/client/context`、`report-streams`、`status-reports`；正文不能改绑设备或冒充实例。恢复保留身份和安装履历，同事务吊销旧凭据、关闭旧流；接入暂停只拒绝 API，不控制现场软件。

登记许可失效后禁止新增登记；原键、原请求与仍有效的原实例秘密可核实原成功结果，不重新创建或占名额。报告按实例/代次/序号去重，当前相同内容重放不刷新接收时间，同序号不同内容返回 REPORT_CONFLICT，旧序号或旧流 applied=false。快照每实例一行，首次安装事实和安装变化另存履历，不为每分钟心跳追加永久通用幂等记录。严格超过五分钟显示状态未知并保留最后事实，接入方至少每分钟调用一次。现场版本允许未关联平台记录，本批 installedReleaseId 非空返回 RESOURCE_NOT_FOUND；可用版本、任务字段为空。

网页与 API 同源，通过 HTTPS 和现有 Cookie/CSRF 使用真实数据。管理写请求携带 `Idempotency-Key`，修改和撤销已有资源另带 `expectedRevision`；当前授权先于重放，合法重放先于旧修订比较。响应不明时页面仅在内存保留原请求及操作键，由人员手动核实，不自动换键或重发；核实时的授权拒绝不能证明原请求已回滚，仍保留原键。密码不写浏览器持久存储。分页默认 50、最大 200；人员按工号/ID，台账及软件按代码或设备编号/ID 固定排序。现场游标绑定主体、部署、筛选、页长和权限修订，默认有效 15 分钟，撤权后旧游标失效。

领域事件仅在当前进程和数据库事务内使用，按显式订阅目录执行本模块规则。缺失处理器、非法归属、重复事件标识、处理循环超限、异常或取消均拒绝提交；每事务默认上限 1000，可由 `DomainEventOptions` 调整。数据库确认提交成功才确认已处理事件；回滚或提交结果未知保留待处理事件，沿用既有幂等核实，不自动重执行。具体注册与事务边界见[框架设计第 7.2 节](docs/软件框架设计.md#72-领域事件)。

Application 通过类型化端口登记三类固定消息；业务、审计、幂等结果及 Outbox 共同提交或回滚。HttpApi 只登记，Worker 使用 MassTransit 8.3.6 原生服务投递到三个固定持久队列；确认丢失允许重复交付，保持原 MessageId 和正文。消费基础在原生事务中保存处理事实、Inbox 完成标记及后续消息，提交确认后才确认领域事件；提交未知只在新 Scope 重新授权并查询，不就地重执行。正式 Worker 保持空业务消费目录，不启动业务消费者或 Inbox 清理；合法目录及持续工作事实目前由测试夹具验证。FND-06、FND-08、FND-09 均为部分通过；详见[框架设计第 8 节](docs/软件框架设计.md#8-事件总线mq-和持久化工作)。

验证覆盖、证据位置和待执行项统一见[软件框架设计第 11 节](docs/软件框架设计.md#11-审阅出口与当前验证状态)。本仓库不包含本机验证产物或真实环境配置。

## 目录与依赖方向

| 目录 | 内容 |
|---|---|
| `src/shared` | SharedKernel 领域基础、Contracts 内层契约、CrossCutting 公共应用管道 |
| `src/modules` | Identity、Releases、Instances、Audit；每个模块的 Core 与 Service 放在同一目录 |
| `src/application` | Application 应用用例与跨模块协调 |
| `src/infrastructure` | EF 持久化与消费事务桥接、Dapper 只读查询、Security 安全技术实现、EventBus Outbox/RabbitMQ 发送和接收适配 |
| `src/hosts` | HttpApi、Worker、Migration 组合根及 ServiceDefaults 公共主机配置 |
| `src/ui/svm-web` | Vue 会话、人员授权、现场导航/维护、软件目录、登记许可与实例详情/履历网页 |
| `src/analyzers`、`src/tests` | 架构分析器与 Architecture、Security、Framework/Business 测试 |
| `build`、`eng` | 引用白名单、依赖/工具链清单和本机开发脚本 |
| `docs` | 七份设计与验收文档 |

内层定义端口，基础设施实现端口，Hosts 注册具体实现。模块 Core、Service 和 Application 不引用数据库、消息或安全技术实现；完整引用图由[软件框架设计第 3 节](docs/软件框架设计.md#3-目录类库和完整引用关系)及 `build/Architecture.xml` 共同约束。

业务仍分 IAM、REL、PKG、INS、TSK、AUD 六个逻辑模块。PKG、TSK 的业务实现尚未开始，其四个空工程已删除；现有 schema、权限及版本化消息契约保留，后续有实际实现时再建立工程。已实现模块的 Core 与 Service 保持独立程序集，类型名、命名空间和依赖边界不变；模块位置见[模块设计第 1 节](docs/模块设计.md#1-模块与调用方向)。

## 文档入口

本仓独立初始化入口为 [AGENTS.md](AGENTS.md)。在 Codex 中将本目录作为项目主目录；不把外层 `1` 或其他项目目录加入同一开发项目。

1. [需求规格说明](docs/需求规格说明.md)
2. [总体架构](docs/总体架构.md)
3. [模块设计](docs/模块设计.md)
4. [详细设计](docs/详细设计.md)
5. [厂家接入文档](docs/厂家接入文档.md)
6. [架构测试与验收要求](docs/架构测试与验收要求.md)
7. [软件框架设计](docs/软件框架设计.md)

字段、路径和状态契约以详细设计为准；厂家文档登记/恢复及上报已按真实接口验证，版本包任务仍属设计说明。Cloud 只提供固定公开提交的架构与工程机制参考，平台不对接其业务、主数据或身份。聊天示例不作为实际厂区/设备数据，文档中的虚构值不得用于播种或默认配置。

## 本机准备

### 工具与编译

已验证的开发环境为 macOS arm64。需要可用的 Node.js（仅用于运行首次引导）、curl、tar、OpenSSL 及 Docker CLI/引擎。工具链清单也提供 macOS x64、Linux arm64/x64 下载项，但不代表这些环境的完整运行验收已经完成。

所有命令均从仓库根目录执行：

```sh
node eng/bootstrap.mjs
eng/dotnet restore SoftwareVersionManagement.sln --locked-mode
eng/dotnet tool restore --configfile NuGet.Config
eng/dotnet build SoftwareVersionManagement.sln --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
eng/npm --prefix src/ui/svm-web ci --ignore-scripts
eng/npm --prefix src/ui/svm-web run build
eng/dotnet build src/hosts/Svm.HttpApi/Svm.HttpApi.csproj --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
.tools/node/bin/node eng/verify-dependencies.mjs
```

网页先构建到 `dist/`，再编译或发布 HttpApi 将构建产物复制到 `wwwroot/`，由同一主机托管。未知 `/api/...` 仍返回 JSON 错误，不落入网页路由。工具版本与摘要由 `build/toolchain.lock.json` 固定；NuGet 版本和许可清单见软件框架设计。脚本只设置本项目的工具和缓存目录，不修改系统 SDK。

### PostgreSQL 与人员 API

当前本机数据库脚本仅支持 `build/postgres.local.json` 固定的 **linux/arm64 镜像及本机 `desktop-linux` Docker context**；脚本会拒绝远程 Docker、错误平台或不属于本项目的容器/卷。请先完成上面的编译，再执行：

```sh
eng/postgres up
eng/postgres status
eng/postgres migrate status
eng/postgres migrate apply
eng/personnel prepare
eng/personnel seed
eng/personnel serve
```

`up` 创建项目独立的 PostgreSQL 容器、卷和角色，仅绑定本机地址。`migrate apply` 和 `seed` 均为显式操作，API/Worker 启动不会自动执行。重复播种不重置现有密码和授权。

OperationResults、BusOutbox、PersonnelAdministrationPermissions、SiteCatalog 及本批新增 `20261009000100_InstanceAccess` 仅在一次性测试库执行。新迁移建立 IAM 接入主体、凭据、登记/恢复许可、登记结果和 INS 实例、快照、安装履历、流结果九张表；不生成默认实例、许可或授权，既有六份迁移不改，快照及只读安全列权限同步。开发库仍只应用 InitialSchemas、PersonnelSessions，升级须另行确认。编译 Migration 后生成包含待执行迁移和权限核对的幂等脚本：

```sh
mkdir -p artifacts/instance-access
eng/postgres migrate script > artifacts/instance-access/instance-access-upgrade.sql
```

首次管理员默认为 `LOCAL-ADMIN`；显示名和随机临时密码位于已忽略的 `.cache/personnel-local/seed.json`，可在首次播种前私下调整。首次登录须改密。禁止将该文件、数据库连接、会话材料或证书私钥加入版本库。

`serve` 启动 `https://127.0.0.1:7443` 的人员 API 及已构建网页；证书为本机生成的自签名证书，未自动加入系统信任。网页实现不代表当前开发库已具备管理所需迁移；开发库升级仍须另行确认。私有 personnel.json 可增加 `management`：`defaultPageSize:50`、`maximumPageSize:200`、`cursorMinutes:15`；省略时使用这些默认值，游标上限 60 分钟。直接运行 Hosts 的配置方式见[框架设计的工程入口](docs/软件框架设计.md#112-工程入口)。

结束开发可执行 `eng/postgres stop`，保留数据卷和私有配置。重新启动数据库后再次执行 `eng/postgres up` 更新本机端口配置；不要删除已有数据库对应的私有凭据。

### 显式厂区配置

现场能力需要私有 `SVM_SITE_CONFIG_FILE`。标识、实际名称和 IANA 时区须由本厂明确提供，无默认厂区或设备。可用 `eng/site prepare --id <稳定UUID> --name <实际厂区名称> --time-zone <IANA时区>` 保存忽略的 `.cache/site-local/site.json`，工具不迁移或播种；再次运行不允许替换已有厂区标识。然后通过 `SVM_SITE_CONFIG_FILE="$PWD/.cache/site-local/site.json" eng/personnel serve` 启动。

JSON 字段为 `siteId`、`siteName`、`siteTimeZone`，可选 `defaultPageSize:50`、`maximumPageSize:200`、`cursorMinutes:15`。缺少配置时现场接口返回 `503 CONFIGURATION_INVALID`；显式配置格式或限额错误使启动失败。首个现场/软件写事务把稳定厂区标识绑定数据库，换成其他标识不能读取或写入旧台账。人员功能沿用既有配置与运行方式。

### 实例接入配置

在同一显式厂区配置下，`SVM_INSTANCE_ACCESS_CONFIG_FILE` 指向私有 JSON，必填 `enrollmentMaxLifetimeSeconds`、`enrollmentMaxCount`、`recoveryMaxLifetimeSeconds`，分别限制登记许可有效期/名额及恢复许可有效期。有效期上限必须为正整数秒，数量上限为 1～100000；取值由本厂明确提供，无生产默认值。每次签发仍填写具体未来到期时间、已有映射设备范围和原因，恢复固定到单实例、名额为 1。缺少配置时仅新签发返回 `503 CONFIGURATION_INVALID`；显式无效配置拒绝启动，已有实例和人员入口保留。文件应只对运行账号可读，不提交仓库。

从现场设备软件卡片进入“登记许可”签发受限许可，安全交付许可 ID、秘密和设备关联；厂家自行生成安装标识和至少 256 位 base64url 实例秘密。网页秘密仅在当前表单内存保留，关闭后清除；查询接口不返回秘密。实例页面按软件查看、按设备编号/IP/状态筛选，详情分开显示安装履历、凭据和 API 接入启停。暂未提供恢复许可列表接口，本次恢复许可签发结果支持直接撤销，操作人员保管其标识及修订。

### RabbitMQ 与专用验证环境

HttpApi/Worker 仅在显式设置 `SVM_MESSAGING_CONFIG_FILE` 时启用消息注册；文件不存在、结构或参数无效时明确失败。未设置时沿用现有运行方式。私有 JSON 文件使用 0600 权限，字段及限制见[框架设计第 8.1 节](docs/软件框架设计.md#81-组件落点和原子提交)，不把凭据写入命令行或仓库。Broker 不可用不阻塞数据库发送意图的登记；启用发送端前须显式完成目标数据库迁移。

消费目录通过 `AddSvmConsumption` 与 `AddSvmMessaging` 使用相同的显式绑定，构建前调用 `ValidateSvmFoundation`。目录限定三类 V1 消息、模块、队列和唯一 Scoped Application 处理器；正式 Worker 当前目录为空。消费并发默认 4、prefetch 16，Inbox 窗口默认 30 分钟，参数有上限。只对确知未提交的瞬时故障短重试 3 次，间隔 1/3/5 秒且每次新 Scope；其余失败进入错误队列，禁止自动回灌。

Migration 私有配置新增 `enableInboxWrites`，默认 `false` 保持发送端权限；显式 `true` 只补充 Inbox 表读写及其 ID 序列权限，不新增结构迁移。消费基础批次只在一次性测试库启用，当时生成的 `artifacts/consumer-outbox-review.sql` 包含既有四份迁移及消费权限，供另行审阅，未在开发库执行。正式消费还须实现可信工作授权适配及所属模块持续去重事实；启用 Inbox 清理不能删除这些业务事实。

本项目验证工具固定 RabbitMQ `4.3.6` 的 linux/arm64 镜像摘要与本机 `desktop-linux` Docker context，只绑定回环地址，管理带本仓归属标签的容器和卷。首次分配的端口在容器停止/启动期间保持；随机测试 vhost 默认 quorum，临时总线队列显式 classic。测试账号只具有所属 vhost 权限，无管理标签。工具不会配置生产 broker。

```sh
eng/rabbitmq up
eng/rabbitmq status
```

验证结束执行 `eng/rabbitmq down`，清理本工具拥有的测试容器、卷和私有配置；`stop`/`start` 用于保留数据的断线恢复验证。私有测试管理材料位于忽略的 `.cache/rabbitmq-local/test-admin.json`。

## 验证方式

测试选择规则只在 [AGENTS.md](AGENTS.md#实施边界) 维护。`eng/test` 默认运行完整 Architecture，Security、Framework 必须提供显式过滤条件；入口只运行已有 Debug 产物，不自动还原、编译、迁移或启动容器。可先用 `--preview` 查看选集：

```sh
eng/test architecture
eng/test framework --filter 'FullyQualifiedName~ClosedAdministrationCompositionBuildsWithFourTypedAdapters' --preview
eng/test framework --filter 'FullyQualifiedName~ClosedAdministrationCompositionBuildsWithFourTypedAdapters'
```

构建与类型检查单独记录。网页发生变化时，使用锁定依赖构建，并指定受影响测试文件，例如人员或台账页面：

```sh
eng/npm --prefix src/ui/svm-web run build
(cd src/ui/svm-web && ../../../eng/npm exec -- vitest run tests/personnel.test.ts tests/catalog.test.ts)
```

涉及真实 PostgreSQL 的选集可通过 `eng/postgres test framework --filter '<显式选集>'` 转发到同一入口，并加载本项目私有配置；该命令不会启动或探测 Docker。也可显式提供 `SVM_TEST_DATABASE_CONFIG_FILE`、`SVM_PERSONNEL_CONFIG_FILE` 及所选用例需要的 `SVM_PERSISTENCE_CONFIG_FILE`。不在命令行传密码，结果仅写入忽略的 `artifacts/test-results/`。

数据库事务、消息投递/恢复或浏览器流程发生相关变化时，再选择对应真实夹具。以下为单个消息隔离用例的调用方式，需先完成 PostgreSQL、RabbitMQ 和 FrameworkTests 构建：

```sh
SVM_TEST_DATABASE_CONFIG_FILE="$PWD/.cache/postgres-local/test-admin.json" \
SVM_TEST_RABBITMQ_CONFIG_FILE="$PWD/.cache/rabbitmq-local/test-admin.json" \
  eng/test framework --filter 'FullyQualifiedName~UnknownContractIsQuarantinedOnceAndTheNextValidMessageStillCommits'
```

真实浏览器用例为 `PersonnelBrowserTests`、`SiteCatalogBrowserTests`、`InstanceBrowserTests`，使用一次性 PostgreSQL、显式虚构厂区、临时 HTTPS 主机及匹配锁定 Playwright 的 Chromium；接入用例另显式配置签发上限。浏览器尚未准备时可显式执行 `(cd src/ui/svm-web && PLAYWRIGHT_BROWSERS_PATH="$PWD/../../../.cache/playwright-browsers" ../../../eng/npm exec -- playwright install --only-shell chromium)`。截图写入忽略的 `artifacts/personnel/browser-*/`、`artifacts/site-catalog/browser-*/`、`artifacts/instance-access/browser-*/`，不保存密码/秘密表单或请求追踪。

恢复用例可能停止/重启本工具拥有的专用 broker、丢弃发送确认或终止本次测试子进程；按所选用例准备并串行使用这些资源。`ConsumptionHarness` 分类只由私有子进程验证工具调用；生产 Worker 没有测试开关。每个用例清理其随机数据库、角色、vhost、账号及私有文件，结束后核对残留；配置只指向本项目测试环境。已有结果和未覆盖项见框架设计第 11 节。

## 仓库与交付边界

工具、依赖缓存、构建产物、本机数据库和凭据不随仓库提交；安装和还原只使用锁定来源。第三方组件版本、许可来源及升级限制见软件框架设计，当前尚未选择本项目代码的开源许可证。

首次提交建立 `main`；后续变更使用 `codex/` 分支和 PR，保留合并提交，禁止强推主分支。代码上传不执行数据库迁移、生产部署或发布安装包。
