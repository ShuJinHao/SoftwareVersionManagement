# Linux 演示部署与跨平台开发

本文交付已有业务的运行入口。单机两逻辑节点用于功能验证；真实 GitLab、厂家安装、容量、异机故障域及生产高可用另验。源码与目标平台证据分别记录，不能把 Mac 容器验证写成原生 Linux x64 验收。

## 开发与正式构建

仓库使用 .NET 8 / C# 12。普通开发允许稳定 SDK 8.0.400 及以上的同 8.0 版本，VS 2022 建议 17.11 或以上的配套版本。VS 需安装能由 IDE 发现的 .NET 8 SDK/工作负载；仓库私有 ZIP 安装不会自动注册为系统 SDK。

正式发布与开发共用上述 SDK 范围，8.0.425 仅为可选复现环境；Node 24.21.0、npm 11.19.0 保持固定。运行策略为 8.0.31 下限、LatestPatch，只允许同 8.0 补丁滚动。服务器运行已发布制品，无需 SDK 或 Node。

公司已经安装兼容 SDK 时，先执行 `eng/bootstrap --node-only` 仅准备 Node/npm，再执行下面的还原、构建与发布命令。`eng/bootstrap` 和 `eng/bootstrap --sdk 8.0.400` 则显式准备可选的仓库 SDK 缓存；不会修改全局 PATH 或替换公司安装。

从仓库根运行：

~~~sh
eng/bootstrap
eng/dotnet restore SoftwareVersionManagement.sln --locked-mode
eng/dotnet build SoftwareVersionManagement.sln --no-restore --disable-build-servers -m:1
eng/publish --output artifacts/releases/my-release
~~~

Windows 原生 PowerShell 使用 eng/bootstrap.ps1、eng/dotnet.ps1、eng/publish.ps1 等同名入口，参数相同。初次引导会下载并核验固定 Node，不要求先安装 Node 或 WSL。工具缓存按 OS/架构/版本隔离，错误身份或实际版本拒绝复用。

传给 PowerShell 脚本的含冒号参数需要整项加引号，避免脚本参数绑定拆分。例如：

~~~powershell
./eng/dotnet.ps1 build SoftwareVersionManagement.sln --no-restore --disable-build-servers '-m:1'
~~~

兼容 SDK：

~~~sh
eng/bootstrap --sdk 8.0.400
SVM_SDK_VERSION=8.0.400 eng/dotnet restore SoftwareVersionManagement.sln --locked-mode
SVM_SDK_VERSION=8.0.400 eng/dotnet build SoftwareVersionManagement.sln --no-restore --disable-build-servers -m:1
SVM_SDK_VERSION=8.0.400 eng/publish --output artifacts/releases/sdk400-release
~~~

Windows 用 $env:SVM_SDK_VERSION='8.0.400' 设置当前进程环境。SDK 选择顺序为 SVM_DOTNET 的显式绝对路径、SVM_SDK_VERSION 对应的已核验仓库 SDK、PATH 中兼容的系统 SDK、已核验仓库缓存。显式选择不可用时直接失败，不静默替换；开发与正式发布使用相同规则。不支持的 SDK 有明确报错，不能关闭 Analyzer、锁定还原或审计门禁。

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

初始化顺序为：创建专用 PostgreSQL 角色/库 → 显式应用 12 份迁移（最后为 HardwareWorkspace） → 显式 seed。API/Worker 均不自动迁移或播种。migration/seed 配置只挂载到一次性 Migration 容器，日常 API/Worker 只获得 writer/reader。完整消费者配置显式启用 Inbox 权限；旧迁移和模型不变。

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
- 数据库未达到 12 份迁移及 HardwareWorkspace 标识、或不可达时未就绪；MQ 中断不导致 liveness 失败，API 仍可保存 Outbox 意图，Worker 消费就绪单独显示。Docker unhealthy 不等于应不断重启进程。
- 证书角色覆盖与完整性增强仍暂缓；当前链验证、指纹、有效性和 mTLS 继续保留。

直接执行 --validate-config 仍需完整挂载配置和准备日志目录；它不验证网络可达性。status、依赖 healthcheck 与后续 HTTPS 流程用于确认网络和数据库事实。

## 查看业务效果

先在未绑定 GitLab 的虚构软件上完成：登录改密 → 明确授权 → 工序/设备/工控机 → 固定硬件软件位置 → 签发位置许可并登记两个实例 → 上传演示 ZIP → 双副本就绪 → 实例 A 安装上报 → 转正式 → 为尚未安装的实例 B 创建 Update → 领取/开始/回执 → 独立安装上报。

本项目提供仅调用正式 API 的模拟接入方，并用真实浏览器查看当前数据：

~~~sh
# 在验证机运行；该机器需要项目工具、网页依赖和固定 Playwright 浏览器。
PLAYWRIGHT_BROWSERS_PATH="$PWD/.cache/playwright-browsers" eng/node src/ui/svm-web/node_modules/playwright/cli.js install chromium
eng/node deploy/linux/verify-hardware.mjs --deployment artifacts/linux-demo/my-demo --results artifacts/test-results/linux-demo
~~~

验证器只允许明确 demo 身份，使用一次性全新台账。它会改初始密码、创建虚构台账、固定安装位置和模拟报告，并在浏览器创建硬件/视觉软件、选择具体工控机更新；不能对真实厂区运行。API 请求验证演示 CA 和主机名；Chromium 仅接受本次生成的服务器叶子公钥 SPKI，不使用全局忽略证书错误。正式用户浏览器应信任企业 CA。

本流程不执行 Windows 软件安装，成功回执与后续上报均是模拟协议事实；不代表厂家现场安装、数据保护或高可用通过。验证失败后保存脱敏阶段记录，私有 hardware-fixture.json 记录每个写入的确认结果。可用 --resume 复用已确认阶段继续；任何未确认写入都会停止，必须先核实原请求，不能删检查点自动重试。整个业务阶段确认完成后可用 --display-only 只查看页面、重拍截图，不重复上传或创建投放。历史 verify.mjs/verify-ui.mjs 为旧许可模型的验证记录，本批使用 verify-hardware.mjs。

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

平台 CI 使用 Windows/Linux x64 与两个 SDK 的加载/构建矩阵；两个 Linux SDK 分别完成正式发布，仅 Linux 8.0.425 运行 Architecture、受影响选集和本批定向 Compose 浏览器流程。VS MSBuild 成功不替代 GUI 实机验收。PR 文档单独更新复用已有代码证据，GitLab 接入示例不承担平台自身 CI。

当前执行结果见框架设计与验收记录；任何尚未完成的目标环境验证须明确标注。

## 网页操作定向验证

任务和版本读取按对象/请求代次失效，旧响应不覆盖新对象；自动轮询不重置已加载分页。投放明细显示读取时间，显式刷新才从首页重载。投放草稿与资料版本独立，封存后固定草稿；人工关闭逐任务重新核实，结果仍为 Unknown。

公共 JSON 读/写默认 15/30 秒，响应正文也在预算内。写入超时保留原键和正文，仅在本页人工核实；展示操作编号与安全 traceId，不持久化密码或请求正文。核实先检查原人员身份和当前会话；换人或会话无法确认时保留原操作但不发送。跨页和浏览器重开恢复尚未实现。

`eng/test web --file tests/deployments.test.ts --filter 'deployment read ownership' --preview` 预览精确文件和名称，`--list` 列出实际匹配；零匹配拒绝执行。省略预览参数才执行测试。网页构建、测试、浏览器证据分别记录。

`verify-ui.mjs` 只用于专用的 full 演示环境：准备参数须明确 task.batchSize 至少 3、resultWaitSeconds 为 1。它使用真实 HTTPS/API 创建明确虚构的界面夹具；延迟、分页缩小和丢响应注入单独记录，不重跑 MQ/文件恢复验收。命令与原演示验证器相同：

~~~sh
eng/node deploy/linux/verify-ui.mjs --deployment artifacts/linux-demo/ui-demo --results artifacts/test-results/linux-ui
~~~

准备成功后，私有 ui-fixture.json 保存安全恢复所需的工作标识。尚未进入后续写入阶段可加 `--resume-browser`；冻结阶段尚未写入且前序三任务事实满足时可加 `--resume-freeze`；原键核实和两任务关闭已完成时可加 `--resume-viewport` 仅复验读取与布局。验证器检查对应检查点，拒绝跳过未知写入；不得重建未知业务或更换原操作键。200% 场景记录为等效 CSS 视口/DPR 验证，不能称为人工浏览器菜单缩放验收。


## 包恢复与本批页面展示

私有包配置 execution 增加可省略的 peerResponseSeconds（默认 15，1～28）和 downloadAuthorizationSeconds（默认 20，2～29），要求前者小于后者。prepare.mjs 同步生成节点和网关参数；授权预算必须低于网关 30 秒等待。前者用于复制建连/响应等待，字节流继续使用 limits.uploadIdleSeconds 的空闲预算，正常大包不设短总时长。复制、健康扫描和下载日志分别运行；首次就绪仍核对双副本，下载可以使用本次已经核验的一个健康副本。企业证书角色增强不在本批。

本批使用干净完整 Release、新的 full 演示目录及明确虚构数据，运行：

~~~sh
eng/node deploy/linux/verify-audit.mjs --deployment artifacts/linux-demo/audit-correctness-20261018 --results artifacts/test-results/audit-correctness/linux-demo
~~~

验证器通过真实 HTTPS/API 准备台账、双副本、Formal 和 Update，再检查页面固定对象、分页、迟到响应和丢响应后的原键人工核实。浏览器故障注入与真实接口分别记录；安装上报来自测试夹具，不代表厂家安装。私有 audit-fixture.json 保存工作标识，已有夹具拒绝重复准备；全部操作已验证后可使用 --display-only 重新截图，只执行读页面。不要删除检查点后重做尚未核实的写操作。

用户查看用的演示单独保留。operator.json 包含本机演示登录材料，不上传仓库；svm.sh stop 暂停容器，svm.sh down 删除该演示的容器/网络并保留绑定目录数据。故障测试资源与这个保留演示分开清理，不能把保留演示记录成资源残留为零。

2026-10-18 实际展示使用 Release 5e8b272（dirty=false），宿主 macOS arm64、服务 Linux arm64，专属项目 svm-demo-35dbe7795749。七阶段局部走查完成；初次键盘断言将 Tab 进入浏览器界面误判，修正验证器后仅以 --display-only 重查页面和焦点，原写入不重跑。完整截图在 artifacts/test-results/audit-correctness/pages.md，测试与资源汇总在同目录 test-selection-results.json。七容器演示保留，故障测试用的一次性 PG/RabbitMQ 已删除；开发库容器保持停止。


## 人员角色与现场范围演示

新部署须显式应用第十三份 RolesAndSiteScopes 迁移。迁移只建立四个可编辑角色，不给已有账号自动分配；管理员在系统管理选择人员、角色及负责工序/设备。新软件不自动增加直接授权；旧 /permissions 只维护旧直接授权，与角色取并集，收窄访问时要一并核对。

本批定向浏览器入口：`eng/node deploy/linux/verify-roles.mjs --deployment artifacts/linux-demo/roles-and-site-scopes --results artifacts/test-results/roles-and-site-scopes/linux-demo`。仅允许明确 full/demo 环境。真实 HTTPS/API 准备两个工序、共用程序、独立实例和正式演示包，四类人员登录查看各自现场、产品权限、角色维护及本人权限；使用测试协议报告，不执行厂家安装。脚本保留私有 roles-fixture.json 和原操作检查点，未知写入停止，不自动重做；--resume 仅继续已确认阶段，--display-only 要求前次全部完成。凭据只在专属目录保管，公开证据无密码。

CI 对本批精确网页选集和 verify-roles 执行浏览器验证；历史 verify-hardware 仅对应原硬件批次。按需复用未改变的消息与文件恢复证据；四角色演示不代表完整生产权限治理或现场验收。保留演示与故障/一次性资源分别登记。
