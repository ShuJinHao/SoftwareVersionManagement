# Linux x64 完整候选版安装

本入口用于统一部署回归，固定 Linux x64 单机、两个逻辑包节点。它不是生产发布或异机高可用承诺。公开候选包不含厂区配置、账号秘密或私钥；接收人须从受控交接记录取得压缩包 SHA-256，核对后再解压。候选名为 `RC-日期-提交短SHA`，完整 SHA、SDK、运行策略、文件摘要、SBOM 和镜像摘要随包保留。

## 运行机条件

- 原生 Linux x64，Docker Engine、Compose v2.24 及以上、Bash、标准 coreutils/tar；不需要 Git、.NET SDK、Node、npm、OpenSSL 或源码。镜像内已带 ASP.NET Core 8.0.31，应用保留 LatestPatch 策略。
- 新的专用数据目录、稳定非 root UID/GID、访问 DNS 或 IPv4、可用端口、不与已有网络重叠的专用 IPv4 子网。模板内端口、子网、容量与时段是填写样例，须逐项核对。
- Docker 守护进程须在目标 Linux 本机；安装器拒绝 arm64，不自动模拟或借用 Docker Desktop context。不得因连接到了其他 Docker 主机而把本机路径当成远程挂载路径。
- 磁盘应容纳镜像、发行包、两份包文件、数据库、日志和备份。生产容量按现场确定，本工具不凭空给生产容量或 RPO/RTO 下结论。

`install.sh` 的目录和参数均使用绝对路径。中文和空格可用；符号链接、换行、冒号和逗号等会影响挂载语义的路径拒绝。不要把私有目录放在公开候选包内部。只允许新建目录；重复 init 不重置密码、身份或数据库。

## 安装顺序

以下 `BUNDLE`、`SETTINGS`、`SITE` 都须改为当前机器的绝对路径。推荐用专用服务用户执行；如果服务 UID/GID 不同于执行者，使用具有该新目录初始化权限的 root 执行。工具只调整本次新建目录的所有权，不调整已有数据。

```bash
BUNDLE=/opt/svm/RC-日期-提交
SETTINGS=/secure/svm-acceptance.json
SITE=/srv/svm/本次新的验收目录

# SHA256SUMS 校验包内文件；压缩包本身的摘要应在解压前另行核对。
bash "$BUNDLE/install.sh" verify
bash "$BUNDLE/install.sh" load
cp "$BUNDLE/acceptance-settings.template.json" "$SETTINGS"
chmod 600 "$SETTINGS"
# 编辑 SETTINGS：厂区、管理员、地址、目录、UID/GID、参数均显式填写。
bash "$BUNDLE/install.sh" check --settings "$SETTINGS"
bash "$BUNDLE/install.sh" init --settings "$SETTINGS"
bash "$BUNDLE/install.sh" build --directory "$SITE"

"$SITE/svm.sh" --offline dependencies
"$SITE/svm.sh" --offline preflight
"$SITE/svm.sh" --offline status
"$SITE/svm.sh" --offline script > /secure/svm-upgrade-review.sql
# 先审阅 SQL 与目标目录。以下两个命令须显式执行：
"$SITE/svm.sh" --offline apply
"$SITE/svm.sh" --offline seed
"$SITE/svm.sh" --offline validate
"$SITE/svm.sh" --offline up
"$SITE/svm.sh" --offline ps
"$SITE/svm.sh" --offline diagnostics
```

初始化只创建私有配置、空目录和专用随机秘密。首次启动 PostgreSQL 建立专用角色/空库；`apply` 应用现有 17 份迁移；`seed` 创建唯一初始管理员。API/Worker 不自动迁移或播种。没有默认工序、设备、软件、实例或模拟上报。数据库名固定 `svm_site`，在本次专用 PostgreSQL 内；不是连接或升级开发库。

`load` 只加载归档并核对实际镜像 ID/架构；`build` 只把已发布程序和本厂区公有 CA 装入运行镜像。构建使用本包中已载入的 ASP.NET 基础镜像、`--pull=false --network none`，不构建源码或还原依赖。生成的 `deployment.offline.env` 与 `images.lock.tsv` 固定实际运行镜像。再次 build 拒绝覆盖；变更发行物须按升级流程另行安排。

初始化失败可能留下 `.initializing` 和部分私有目录。它不是可启动部署，不自动删除或续做。核对失败原因后只清理本次未使用目录，选新目录重新初始化；不得对正在使用的数据库套用该流程。清理过程中不要输出文件内容或把它们上传到问题单。

## 登录、信任与 GitLab

入口地址、工号和随机临时密码在私有 `operator.json`。首次登录必须改密；初始权限和后续授权见 [账号与权限](ACCOUNTS.md)。不在命令输出、公开制品或文档中提供密码。

必须明确选择 `certificateMode=acceptance-test`，有效期 1～90 天。初始化为本次部署生成独立 CA、正确 SAN 的 HTTPS 证书及节点 mTLS 身份。将 `private-certificates/root.pem` 的公有根通过受控方式导入测试电脑信任库，使用配置的域名或 IP 访问；不得跳过 TLS 验证。CA 私钥、PFX 和密码始终保留在私有目录，备份须一并保护。该证书模式仅用于验收；企业签发、轮换和证书角色增强不由此验收替代。

GitLab 默认未配置。启用时，在 `runtime/node-a`、`runtime/node-b` 放置一致的私有 `gitlab.json`；企业公有 CA 可放到各节点 certs，配置 `trustCertificatePath=/svm/config/certs/企业根.pem`。在 `deployment.env` 和 `deployment.offline.env` 同步设置 `SVM_GITLAB_CONFIG_FILE=/svm/config/gitlab.json`，再 validate 并显式重启。配置格式、令牌和流水线示例见 `gitlab/README.md`（仓库中见 `eng/gitlab/README.md`）。秘密只能进入私有运行配置；网页只维护允许连接下的项目/包绑定。未联通或尚未核验不能记为“连接成功”。

## 停止、备份和交接

`svm.sh --offline stop` 停止并保留数据；`up` 再启动。`down` 移除本项目容器/网络，保留数据。任何磁盘清理只按本次 deployment.json 和项目标识进行，不用 docker prune。

冷备份、隔离复核及人工开放见 [运维与恢复](OPERATIONS.md)。恢复后离线基础镜像可由同一候选包 `load` 提供；使用恢复目录的原始发行材料、Dockerfile 和新项目 ID 组装，恢复围栏仍须核实后显式开放。不要把公开候选包与私有备份混在一个公共下载包中。

完整回归执行 [统一回归记录](REGRESSION.md)。真实 GitLab、厂家安装与数据保护、实际 PLC 使用、公司 VS GUI、目标 Linux 条件和生产容量/故障域分别记录证据。候选版不包含通用外部管理系统凭据、跨页未知操作恢复、完整告警通知；这些边界不能在验收记录中隐藏。
