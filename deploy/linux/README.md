# Linux 演示部署与跨平台开发

本文交付已有业务的运行入口。单机两逻辑节点用于功能验证；真实 GitLab、厂家安装、容量、异机故障域及生产高可用另验。源码与目标平台证据分别记录，不能把 Mac 容器验证写成原生 Linux x64 验收。

## 开发与正式构建

仓库使用 .NET 8 / C# 12。普通开发允许稳定 SDK 8.0.400 及以上的同 8.0 版本，VS 2022 建议 17.11 或以上的配套版本。VS 需安装能由 IDE 发现的 .NET 8 SDK/工作负载；仓库私有 ZIP 安装不会自动注册为系统 SDK。

正式发布固定 SDK 8.0.425、Node 24.21.0、npm 11.19.0。运行策略为 8.0.31 下限、LatestPatch，只允许同 8.0 补丁滚动。服务器运行已发布制品，无需 SDK 或 Node。

从仓库根运行：

~~~sh
eng/bootstrap
eng/dotnet restore SoftwareVersionManagement.sln --locked-mode
eng/dotnet build SoftwareVersionManagement.sln --no-restore --disable-build-servers -m:1
eng/publish --output artifacts/releases/my-release
~~~

Windows 原生 PowerShell 使用 eng/bootstrap.ps1、eng/dotnet.ps1、eng/publish.ps1 等同名入口，参数相同。初次引导会下载并核验固定 Node，不要求先安装 Node 或 WSL。工具缓存按 OS/架构/版本隔离，错误身份或实际版本拒绝复用。

兼容 SDK：

~~~sh
eng/bootstrap --sdk 8.0.400
SVM_SDK_VERSION=8.0.400 eng/dotnet restore SoftwareVersionManagement.sln --locked-mode
SVM_SDK_VERSION=8.0.400 eng/dotnet build SoftwareVersionManagement.sln --no-restore --disable-build-servers -m:1
~~~

Windows 用 $env:SVM_SDK_VERSION='8.0.400' 设置当前进程环境。要使用已有系统 SDK，可显式设置 SVM_DOTNET 为 dotnet 可执行文件的绝对路径；正式发布仍核验 8.0.425。不支持的 SDK 有明确报错，不能关闭 Analyzer、锁定还原或审计门禁。

发布入口按顺序构建前端、还原并发布 HttpApi/Worker/Migration，核验页面资源、许可、runtimeconfig，输出 release.json 的代码提交、dirty 状态、锁文件与 SHA-256。默认拒绝有未提交改动的工作区；仅显式 --allow-dirty 可生成标记 dirty=true 的开发验证制品，不可冒充干净发布。不要复制 Mac 的 bin/obj/node_modules 到 Linux。

## 环境与支持边界

首条完整目标是 Linux x64、普通 Docker Engine 与 Compose 2.24.0 或以上（使用显式清除可选配置的 !reset）；记录实际发行版、引擎、Compose 和 CPU。配置兼容 Linux arm64，但只有实际目标验收后才能标为已验证。Windows 本批验证开发入口，GUI 加载仍需 VS 实机记录。

数据库、RabbitMQ、Nginx 和 .NET 镜像使用既有固定版本/平台摘要；不会自动切换 Docker context。开发辅助工具支持当前或 SVM_DOCKER_CONTEXT 指定的本地 Unix socket/Windows named pipe，服务端部署使用下述 Compose，不启动 eng/personnel 的 Debug 本机入口。

构建机需要本仓工具、Git、OpenSSL、Docker；离线 Linux 运行机仅需受支持 Engine/Compose、Bash、足够磁盘和匹配的私有配置。安装系统组件由现场环境负责，本文不对未知发行版执行 apt/yum。

## 准备独立演示

prepare 只提供显式 --demo 模式，创建虚构厂区、随机身份及私有凭据，不使用聊天中的厂区设备。不会覆盖已有输出，也不会连接 svm_dev。正式厂区应按模板配置自己的稳定身份、参数、CA 和证书；本工具生成的 30 天演示 CA 不作为企业生产证书。

1. 复制 demo-settings.json 到私有目录，明确 mode（full/preview）、CPU、域名/IP、端口、绑定地址、独立网段以及任务和包参数。
2. 若要从另一台电脑访问，显式修改 publicBind 为服务器监听地址，publicHost 为实际可解析名称，生成的服务器证书会包含该名称。示例默认仅回环。
3. 选定稳定的非 root serviceUid/serviceGid。默认采用准备用户的 UID/GID；转交另一台机器时，保持这个专用账号及文件所有权，不给目录开放 777。
4. 网段不能与现场已有网络重叠；gatewayAddress 必须位于该网段。修改访问名称、节点或信任材料应生成并审阅新配置，不能只改 DNS 绕过 TLS。

~~~sh
eng/node deploy/linux/prepare.mjs --demo --settings deploy/linux/demo-settings.json --release artifacts/releases/my-release --output artifacts/linux-demo/my-demo
cd artifacts/linux-demo/my-demo
./svm.sh build
./svm.sh pull
./svm.sh dependencies
./svm.sh preflight
./svm.sh status
./svm.sh script > upgrade.sql
# 审阅 SQL；以下明确作用于该新建演示库。
./svm.sh apply
./svm.sh seed
./svm.sh validate
./svm.sh up
./svm.sh ps
~~~

svm.sh 可在准备好的 Linux 目录独立运行，不依赖源码、SDK 或 Node。prepare 对发布物摘要进行复核后，生成独立构建上下文与运行配置。build 仅以固定 ASP.NET 运行镜像构建，不在镜像中安装开发工具。

初始化顺序为：创建专用 PostgreSQL 角色/库 → 显式应用 11 份历史迁移 → 显式 seed。API/Worker 均不自动迁移或播种。migration/seed 配置只挂载到一次性 Migration 容器，日常 API/Worker 只获得 writer/reader。完整消费者配置显式启用 Inbox 权限；旧迁移和模型不变。

登录资料在私有 operator.json；初始账号仅有播种规则规定的权限，首次登录改密，再按职责明确分配资产和软件权限。工具不将真实账号或凭据输出到公共日志。不要公开这个目录、operator.json、连接配置、CA 私钥或服务私钥。

preview 只启动管理 API 与数据库，直接通过 HTTPS 访问；没有包、任务或 Worker。full 启动两个 API/Worker、RabbitMQ 与 Nginx，访问主 HTTPS 端口；第二节点 HTTPS 端口只绑定回环以验证共享会话。

## 网络、文件与探测

- 容器内使用 api-a/api-b/rabbitmq 服务 DNS、独立网段和明确 TLS 名称；Nginx 通过显式 Docker DNS 和共享 upstream 区每 5 秒刷新地址，节点重新分配 IP 后无需重启网关。
- RabbitMQ definitions 的 vhost metadata.default_queue_type 明确为 quorum；preflight 从实际 broker 查询确认，临时总线队列仍显式 classic。
- 公共 API 使用端到端 HTTPS，复制/网关内部授权使用原有 mTLS。Nginx 验证上游证书。
- 只有明确 KnownProxies 的单跳 X-Forwarded-For 会生效；Nginx 覆盖外来转发头，端到端 HTTPS 不从转发头推断身份或协议。
- API/Worker 以固定非 root UID/GID 运行。包目录可读写；Nginx 只读副本、写日志，Worker 日志挂载只读。
- DataProtection 的 svm/ ApplicationName、保护证书与数据库密钥跨节点共享且稳定，不依赖安装路径。
- SVM_HOST_CONFIG_FILE 提供公用 HTTPS 证书、KnownProxies 和私有 HealthPort。--validate-config 校验配置/注册后退出，不启动监听、消费者、业务循环或 DDL。
- /health/live、/health/ready 仅在独立私有监听提供。--healthcheck 从容器内访问，运行镜像不需 curl/Node。
- 数据库未迁移/不可达时未就绪；MQ 中断不导致 liveness 失败，API 仍可保存 Outbox 意图，Worker 消费就绪单独显示。Docker unhealthy 不等于应不断重启进程。
- 证书角色覆盖与完整性增强仍暂缓；当前链验证、指纹、有效性和 mTLS 继续保留。

直接执行 --validate-config 仍需完整挂载配置和准备日志目录；它不验证网络可达性。status、依赖 healthcheck 与后续 HTTPS 流程用于确认网络和数据库事实。

## 查看业务效果

先在未绑定 GitLab 的虚构软件上完成：登录改密 → 明确授权 → 工序/设备/软件/映射 → 登记两个实例 → 上传演示 ZIP → 双副本就绪 → 实例 A 安装上报 → 转正式 → 为尚未安装的实例 B 创建 Update → 领取/开始/回执 → 独立安装上报。

本项目提供仅调用正式 API 的模拟接入方，并用真实浏览器查看当前数据：

~~~sh
# 在验证机运行；该机器需要项目工具、网页依赖和固定 Playwright 浏览器。
PLAYWRIGHT_BROWSERS_PATH="$PWD/.cache/playwright-browsers" eng/node src/ui/svm-web/node_modules/playwright/cli.js install chromium
eng/node deploy/linux/verify.mjs --deployment artifacts/linux-demo/my-demo --results artifacts/test-results/linux-demo
~~~

验证器只允许明确 demo 身份，使用一次性全新台账。它会改初始密码、创建虚构业务记录并停止/恢复该演示的部分组件；不能对真实厂区运行。API 请求验证演示 CA 和主机名；Chromium 仅接受本次生成的服务器叶子公钥 SPKI，不使用全局忽略证书错误。正式用户浏览器应信任企业 CA。

本流程不执行 Windows 软件安装，成功回执与后续上报均是模拟协议事实；不代表厂家现场安装、数据保护或高可用通过。验证失败后保存脱敏阶段记录；更新已经成功时，可显式在原命令末尾添加 --verify-existing，只读取已完成的虚构业务事实、重启所属节点/消息组件并验证浏览器。该模式不会重复上传或创建投放；未知业务提交不能使用此模式猜测成功。

## 停止、升级与清理

~~~sh
./svm.sh stop     # 保留容器、数据库、包、日志和私有材料
./svm.sh up
./svm.sh down     # 移除该项目容器/网络，保留绑定目录
~~~

升级前记录 release.json、配置、DB 状态并备份演练；先明确执行需要的迁移，再换发布镜像。回切前确认 schema 和配置兼容，不能只改镜像标签承诺任意数据库降级。不得在 API 启动时自动追迁移。

一次性验证结束先 down，再按 deployment.json 资源清单清理该随机项目的绑定目录和私有材料。用于用户查看效果的环境若保留，单独登记保留清单及停止/启动办法，不报告残留为零。不要 docker prune 或清理其他项目。

离线交付在联网构建机执行 ./svm.sh export，生成 images.tar、归档 SHA-256、images.lock.tsv 和 deployment.offline.env。清单将原审定镜像引用关联到镜像 ID、目标架构及本演示专属标签，避免 docker save/load 丢失 RepoDigests 后无法按摘要名启动。

将生成目录（包含私有配置和所需初始空数据目录）通过受控通道交给 Linux 运行机，保持专用 UID/GID 和权限。运行：

~~~sh
./svm.sh import                  # 核验归档摘要，再逐镜像核对 ID 与架构
./svm.sh --offline dependencies
./svm.sh --offline preflight
./svm.sh --offline script > upgrade.sql
./svm.sh --offline apply         # 仅明确的新演示库
./svm.sh --offline seed
./svm.sh --offline validate
./svm.sh --offline up
~~~

离线入口使用专属标签且禁止启动时拉取；不会构建源码或下载 SDK/NuGet/npm。不要将 images.tar、operator.json、连接配置或私钥上传 GitHub/公共 CI artifacts。归档和镜像身份校验不代替受控交接的来源信任。

## 按需测试与持续验证

唯一固定测试门禁是完整 Architecture；其他选集先 eng/test --preview。tools 组也必须显式 --filter。构建和类型检查不冒充测试，文档/提交/纯合并不重跑代码测试。

平台 CI 使用 Windows/Linux x64 与两个 SDK 的加载/构建矩阵，仅一个 Linux 任务运行 Architecture 和受影响选集、完整发布与 Compose 演示。VS MSBuild 成功不替代 GUI 实机验收。PR 文档单独更新复用已有代码证据，GitLab 接入示例不承担平台自身 CI。

当前执行结果见框架设计与验收记录；任何尚未完成的目标环境验证须明确标注。
