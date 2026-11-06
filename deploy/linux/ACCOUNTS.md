# 候选版账号与权限交接

初始管理员由显式 `seed` 创建，只有 `identity.manage` 和 `software.create` 两项既有直接授权，必须首次改密。播种重跑不重置密码、不恢复撤销权限；系统保留最后一名有效人员管理员保护。工号不可修改，不物理删除历史主体。

在“系统管理 → 人员/角色”分配角色和负责范围。四个初始角色可编辑，迁移不自动分配；角色名称不是授权凭据。范围可以是全厂、工序或设备，硬件/安装位置继承设备范围。角色统一修改对所有分配人员生效，各角色的操作只能在它自己的范围生效。

| 职责 | 应明确配置的权限和范围 |
|---|---|
| 人员管理员 | identity.manage；角色、人员及授权管理，不自动获得产品发布权 |
| 台账维护 | 现场 asset.read/create/manage、binding.manage；限定负责工序/设备，全厂创建权才可建工序 |
| 程序工程师 | 负责现场的查看、关联、接入；具体产品的 software.read、release.upload；GitLab 绑定另选 release.source.manage |
| 现场运维 | 负责现场的 instance.read/manage、deployment.create/control；回退另选 deployment.rollback，关闭未知另选 task.closeUnknown |
| 产品发布人员 | 指定软件 release.publish/disable；不得以工序运维范围代替共享产品授权 |
| PLC 人员 | 指定库 release.upload/publish/disable、plc.uploadCredential.manage；具体控制器 plc.verify、plc.usage.record 分别授权 |
| 清理与审计 | 指定软件 package.clean、audit.read；清理不绕过任务、下载和副本事实保护 |
| 只读人员 | 指定现场查看或明确产品 software.read；现有 software.read 包含下载能力 |

软件创建不自动给创建者增加产品权限。新关联的软件可按现场角色可见，但发布、停用、来源管理等仍须明确产品授权。旧直接授权与角色取并集，收窄范围时同时核查页面折叠的既有直接授权，避免误认为只撤角色就已收权。

独立实例凭据、PLC 上传凭据、GitLab 只读连接秘密和人员 Cookie 各自有身份边界。人员撤权不自动吊销已经签发的实例凭据或登记许可，须按需要显式撤销；过期/吊销不恢复旧流或旧会话。PLC 上传凭据不能发布或登记现场使用。所有秘密仅在受控私有资料中交接，不进入 Git/截图/回归问题单。

建议统一回归建立四个明确测试人员及有限的虚构/批准范围，验证同一软件跨设备的隔离，并单独验证产品管理权。不要复制演示账号到真实厂区。本人改密与“我的权限”位于账户菜单，首次改密限制必须保留。
