# 决策 0002：Runtime 使用暂存目录和事务恢复更新

## 背景

直接在 `dsh-runtime` 内执行 pnpm 更新会产生半新半旧目录。安装中断、进程终止、断电或文件锁都可能让客户端下次启动时无法判断目录是否可用。

## 决策

Runtime 更新采用同级暂存目录和可恢复事务：

```text
dsh-runtime                    当前版本
dsh-runtime-staging            新版本安装区
dsh-runtime-previous           更新前版本
dsh-runtime-transaction.json   交换事务状态
```

1. 在 `dsh-runtime-staging` 独立安装目标 DSH 版本。
2. Runtime 使用 pnpm 的 `nodeLinker: hoisted` 扁平依赖布局。pnpm 11 只从 `pnpm-workspace.yaml` 读取此项，避免 `node_modules/.modules.yaml` 固定 staging 的绝对路径。
3. 私有 pnpm 固定到已验证版本；Runtime 布局版本变化时先迁移 pnpm 与依赖目录，再安装 DSH。`allowBuilds` 显式许可 DSH 必需的本机模块构建。
   启动迁移保留已安装的精确 DSH 版本，不查询或安装 `latest`；只有用户显式执行 Runtime 更新才会改变 DSH 版本。
4. 校验入口文件、版本元数据和必要依赖；失败时删除 staging，当前 Runtime 不变。
5. 写入 `Prepared` 状态后停止客户端拥有的 Runtime 进程。
6. 将当前目录改名为 `dsh-runtime-previous`，写入 `ActiveMoved`。
7. 将 staging 改名为 `dsh-runtime`，写入 `Promoted`。
8. 启动新 Runtime 并执行健康检查。
9. 健康检查成功后删除 previous 并清理事务记录；失败则交换回 previous。

同一磁盘内目录改名是原子操作，但两个改名不能组成单一系统事务。因此事务记录和启动恢复是协议的一部分：客户端启动时会根据事务阶段和目录实际存在情况恢复活动目录。

## 边界

- `dsh-home` 不参与 Runtime 目录交换，插件、会话、凭据和配置保持不变。
- 只终止 DSH-Sharp 自己启动的进程，不触碰外部或源码部署的 DSH。
- 删除旧目录失败时保留目录并记录诊断，不影响当前 Runtime 使用。

## 验证

测试必须覆盖 staging 安装失败、入口缺失、交换中断、启动恢复、健康检查失败回滚和文件锁异常。
