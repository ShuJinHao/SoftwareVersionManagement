# SoftwareVersionManagement

面向隔离厂区的上位机软件版本管控平台。目标交付为 Linux 上的 .NET 8 服务端、网页管理端、管理 API、接入 API 和厂家接入文档。

平台管理版本、安装包、投放任务和上报事实；接入方通过 API 获取任务，负责本机安装、回退及数据库和日志保护。业务规则以[需求规格说明](docs/需求规格说明.md)为准。

## 当前状态

项目处于基础框架开发阶段，尚未完成系统验收或生产部署。

| 已有实现 | 后续实现 |
|---|---|
| 28 个 C# 项目与网页骨架、固定工具链和依赖锁、编译期架构检查 | 网页业务功能与账号管理 |
| DDD 基础类型、IOC、CQRS 请求分类/验证/授权基础 | 系统凭据、受管实例登记及状态上报 |
| PostgreSQL 工作单元、只读连接、显式迁移与人员播种 | 软件/版本、安装包、批量任务和部分回退 |
| 人员登录、本人改密、退出、共享会话及必要审计 | 领域事件派发、Outbox/Inbox、RabbitMQ |
| 持久化幂等协调器、六模块操作结果存储及提交结果核实 | 业务 HTTP 幂等接入及完整事务/消息验收 |
| 密码哈希、随机凭据校验及密钥保护证书加载 | 文件副本与清理、完整日志/观测/健康检查接入 |

会话接口为 `GET/POST/DELETE /api/v1/session` 和 `POST /api/v1/session/password`。除已开放的人员会话及显式播种用例外，其余业务 Command 继续禁用。`Svm.EventBus`、Worker 及网页目前包含未实现的能力；依赖已安装不代表消息、页面或业务流程已经接通。

验证覆盖、证据位置和待执行项统一见[软件框架设计第 11 节](docs/软件框架设计.md#11-审阅出口与当前验证状态)。本仓库不包含本机验证产物或真实环境配置。

## 目录与依赖方向

| 目录 | 内容 |
|---|---|
| `src/shared` | SharedKernel：DDD 基础类型 |
| `src/core` | 身份、版本、包、实例、任务、审计六个领域模块 |
| `src/services` | 内层契约、CQRS 管道、应用编排和六模块服务 |
| `src/infrastructure` | EF 持久化、Dapper 只读查询、Security 安全技术实现、EventBus 骨架 |
| `src/hosts` | HttpApi、Worker、Migration 组合根及 ServiceDefaults 公共主机配置 |
| `src/ui/svm-web` | Vue 网页工程骨架 |
| `src/analyzers`、`src/tests` | 架构分析器与 Architecture、Security、Framework/Business 测试 |
| `build`、`eng` | 引用白名单、依赖/工具链清单和本机开发脚本 |
| `docs` | 七份设计与验收文档 |

内层定义端口，基础设施实现端口，Hosts 注册具体实现。Core、Services 和 Application 不引用数据库、消息或安全技术实现；完整引用图由[软件框架设计第 3 节](docs/软件框架设计.md#3-目录类库和完整引用关系)及 `build/Architecture.xml` 共同约束。

## 文档入口

本仓独立初始化入口为 [AGENTS.md](AGENTS.md)。在 Codex 中将本目录作为项目主目录；不把外层 `1` 或其他项目目录加入同一开发项目。

1. [需求规格说明](docs/需求规格说明.md)
2. [总体架构](docs/总体架构.md)
3. [模块设计](docs/模块设计.md)
4. [详细设计](docs/详细设计.md)
5. [厂家接入文档](docs/厂家接入文档.md)
6. [架构测试与验收要求](docs/架构测试与验收要求.md)
7. [软件框架设计](docs/软件框架设计.md)

字段、路径和状态契约以详细设计为准；厂家文档示例仍属设计说明。Cloud 的参考链接固定到公开提交，不需要同时检出 Cloud 工程。

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
.tools/node/bin/node eng/verify-dependencies.mjs
```

工具版本与摘要由 `build/toolchain.lock.json` 固定；NuGet 版本和许可清单见软件框架设计。脚本只设置本项目的工具和缓存目录，不修改系统 SDK。

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

本批新增 OperationResults 迁移仅在一次性测试库执行，现有开发库尚未升级。升级前可在编译 Migration 后生成脚本并审阅；执行仍须另行确认：

```sh
mkdir -p artifacts
eng/postgres migrate script > artifacts/operation-results-upgrade.sql
```

首次管理员默认为 `LOCAL-ADMIN`；显示名和随机临时密码位于已忽略的 `.cache/personnel-local/seed.json`，可在首次播种前私下调整。首次登录须改密。禁止将该文件、数据库连接、会话材料或证书私钥加入版本库。

`serve` 启动 `https://127.0.0.1:7443` 的人员 API；证书为本机生成的自签名证书，未自动加入系统信任，网页登录流程尚未接入。直接运行 Hosts 的配置方式见[框架设计的工程入口](docs/软件框架设计.md#112-工程入口)。

结束开发可执行 `eng/postgres stop`，保留数据卷和私有配置。重新启动数据库后再次执行 `eng/postgres up` 更新本机端口配置；不要删除已有数据库对应的私有凭据。

## 验证方式

先编译受影响项目，再运行对应分类。基础脚本提供：

```sh
eng/postgres test architecture
eng/postgres test security
eng/postgres test framework
```

其中 `framework` 选择 Persistence、Composition、HostRuntime、Personnel 和 Idempotency 的 Business 用例，涉及真实 PostgreSQL、一次性测试库和本机 HTTPS 进程；按改动范围选择，不代表全量或系统验收。细粒度执行使用 `eng/dotnet test --filter`，需提供 `SVM_TEST_DATABASE_CONFIG_FILE`，人员用例还需 `SVM_PERSONNEL_CONFIG_FILE`，分别指向本机生成的 test-admin.json、personnel.json 私有文件；不在命令行传入密码。结果写入忽略的 `artifacts/`。

## 仓库与交付边界

工具、依赖缓存、构建产物、本机数据库和凭据不随仓库提交；安装和还原只使用锁定来源。第三方组件版本、许可来源及升级限制见软件框架设计，当前尚未选择本项目代码的开源许可证。

首次提交建立 `main`；后续变更使用 `codex/` 分支和 PR，保留合并提交，禁止强推主分支。代码上传不执行数据库迁移、生产部署或发布安装包。
