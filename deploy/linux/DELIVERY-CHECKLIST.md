# 统一候选版交接清单

由部署人员填写真实输入；本文不提供默认厂区、设备、账号或生产参数。`prepare.mjs --demo` 仅用于明确虚构的独立演示。统一候选使用 [ACCEPTANCE](ACCEPTANCE.md) 在合并后的干净 main 封装，先完成下表，再在目标环境安装并由用户一次性回归；账号说明和可填写问题格式分别见 [ACCOUNTS](ACCOUNTS.md)、[REGRESSION](REGRESSION.md)。

| 类别 | 必须交接的输入及材料 | 校验位置 |
|---|---|---|
| 发行物 | 代码 SHA、PR、release.json、锁文件摘要、SDK 实际版本、文件 SHA-256、SBOM 与许可；dirty=false | eng/publish、镜像清单 |
| 目标环境 | Linux 发行版、amd64/arm64、Docker/Compose、稳定非 root UID/GID、域名、端口、独立网段、磁盘容量与挂载 | Compose 配置、validate、diagnostics |
| 厂区 | 固定 siteId、siteName、siteTimeZone；各节点一致 | runtime/node-*/site.json，SVM_SITE_CONFIG_FILE |
| 数据库 | 数据库名、bootstrap/迁移/写/只读角色及秘密；reader 强制只读，运行节点不挂迁移凭据 | postgres/init.sql、init/migration.json、runtime/node-*/persistence.json |
| 人员与身份 | applicationName、共享保护证书及密码、人员策略；实际首个管理员工号/姓名/临时密码；首次改密 | personnel.json、init/seed.json；显式 seed |
| HTTPS 与节点 | 企业信任根、服务 DNS/SAN、服务器/Peer/Gateway/Collector 证书及指纹、受信代理、固定 NodeId/SubjectId | host.json、package.json、gateway/nginx.conf；保留既有 HTTPS/mTLS 校验 |
| 包和消息 | 两个独立包目录、日志目录、大小/超时/轮询/租约参数；厂级 vhost、受限消息账户、服务端名称、固定队列 | package.json、messaging.json、SVM_PACKAGE_CONFIG_FILE/SVM_MESSAGING_CONFIG_FILE |
| 实例与 PLC | 登记/恢复许可上限，PLC 上传凭据最大期限 | instances.json、plc.json；不自动加权 |
| 任务 | 当地允许时段、批量、失败阈值、结果等待、封存上限与租约 | tasks.json，SVM_TASK_CONFIG_FILE |
| GitLab | 允许连接/项目/包、只读 API 和取包凭据、企业证书；数据库绑定接管记录 | gitlab.json，SVM_GITLAB_CONFIG_FILE；[接入说明](../../eng/gitlab/README.md) |
| 恢复 | 停写窗口、异机备份位置、加密/访问控制、源端隔离责任人、核实证据、RPO/RTO 实测 | [运维与恢复](OPERATIONS.md) |
| 验收 | 管理员/工程师/运维/只读测试人员、虚构或批准测试机台、厂家夹具版本、预期/实际结果及问题单 | 下述整体回归清单 |

所有私有 JSON 和证书采用 0600，目录 0700，日志目录只要求 Worker 可读，不额外给写权限。禁止将 secrets 放入公开 Compose、Git、截图和 PR。真实厂区不得复制演示 siteId、DEMO-ADMIN、固定演示参数或 30 天 CA 当成企业配置；运行端和备份必须保存同一身份材料。

Compose 已提供全部既有能力的配置入口。GitLab 若启用，在两节点的只读运行配置目录放置一致的私有 gitlab.json，并在私有 deployment.env 中设置 `SVM_GITLAB_CONFIG_FILE=/svm/config/gitlab.json`。缺配置保持关闭，地址/Token 不能由网页任意填写。执行 validate、检查脱敏来源状态，再安排真实 Tag/流水线联调。配置允许目录变更不覆盖网页绑定历史。

初始化顺序固定为专用角色/库 → 审阅并显式应用十七份迁移（PackageCleanup）→ 显式播种 → validate → 启动 → 私有就绪/诊断。运行时只有 .NET 8.0.31 及同系列向上补丁需要；SDK/Node 不安装到运行容器。备份/恢复使用已审定镜像和新隔离目录，不把旧开发库直接拷贝后视为完成初始化。

统一候选版整体回归由用户集中执行：

1. 空环境部署、迁移和登录改密；角色/范围、共享软件、候选和数量的隔离。
2. 工序 → 设备 → 工控机/PLC → 程序的创建、关联、反查及分页。
3. 真实 GitLab 来源绑定、正式 Tag 导入、双副本、下载、同 Tag 冲突和历史保留。
4. 实例固定位置登记、恢复/吊销、分钟报告、离线未知和实际安装历史。
5. 指定机台更新、批次/阈值/暂停、幂等及未知提交核实、许可撤权、迟到回执隔离。
6. 兼容声明及部分回退；接入方真实安装、数据库和日志保护分别记录。
7. PLC 网页/API 归档、验证转正式、下载、人工使用、更正及历史；不测试不存在的在线下装。
8. 包清理条件、未确认下载保护、双节点失败恢复及历史留存。
9. 私有诊断、日志脱敏、服务重启、冷备份、隔离恢复和人工开放；生产容量/恢复目标另按现场标准验收。

每项记录候选 SHA、环境、操作者、实际结果和证据。修复先做定向验证，再按影响决定复验范围。公司 VS GUI、真实 GitLab 和厂家协议夹具、生产异机故障域不得相互替代。本批不实现管理系统通用凭据、证书角色增强、跨页面未知操作恢复或生产监控通知平台；是否属于首发范围须在候选冻结记录中明确，不能以“开发完成”掩盖这些边界。
