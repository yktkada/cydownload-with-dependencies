# 设计说明

## 目标

把 Cydia 源里的 `.deb` 与其依赖闭包离线归档，导出**依赖有序**的 deb 集合，供刚刷完机的
iOS 4–8 设备通过 `Cydia/AutoInstall` 安装。程序是纯客户端：不托管包、不访问设备。

## 分层

```
src/CyDownload.Core    纯逻辑（无 WPF 依赖，可单测）
  Deb/                 Debian 语义：版本比较、版本约束、依赖表达式、822 控制文件
  Repos/               源描述、Release/索引抓取、内置源与内置机型
  Index/               SQLite 存档库、迁移、包/资料库仓储、内容寻址 pool
  Resolve/             依赖求解（目标设备、base 基线、回溯 DFS、诊断）
  Fetch/               deb 下载与校验、压缩嗅探、deb 结构检查
  Export/              拓扑排序、导出（只复制）、资料库导出
src/CyDownload.App     WPF 界面（MVVM）+ 设置/日志/崩溃上报
tools/CyDownload.Probe 命令行自检台（fetch / resolve / export / repair）
tests/                 单元测试（版本比较向量、真实索引与 deb 夹具）
```

线程约定：界面线程不做阻塞 I/O；所有网络/磁盘/SQLite 调用都在异步路径里，进度用 `IProgress<T>` 回到界面。

## 包身份

一个包/一条数据库项目的身份是**四元全等**：`(package_id, version, architecture, repo)`。
同一个包同版本可能同时来自多个源（例如 saurik 与另一个镜像），它们是不同的行，但内容相同的 deb 在 pool 里只存一份。

## 存档库（SQLite）

主要表：

- `repos` —— 源（地址、布局 dists/flat、suite/component、优先级、启用开关、上次抓取时间）
- `packages` —— 包索引行（含 depends/predepends/provides、大小、sha256、min_ios 等）
- `provides` —— `Provides:` 拆表，便于按虚拟包名反查
- `archive_pool` —— 下载台账：包四元 → pool 对象相对路径 + 内容 sha256
- `library_groups` / `library_group_items` —— 资料库分组（一次导出/下载 = 一个 run；每个根插件一组，组内是整个闭包）

库结构有版本号，`Open` 按版本逐级迁移（加列/重建表都在事务里）。

## 内容寻址 pool

`pool/objects/<sha256>.deb`：同一份字节只落一份。写对象先 `.part` 再原子改名；
复用已有对象前校验长度，不一致就重写。台账多行可以指向同一个对象，清理时按引用判断。

## 求解

输入：目标包名 + 目标设备（机型、iOS 版本、build、cfversion、内核版本、架构）。

- **base 基线**：可假设「刚刷完机」的 31 包预设，或假设什么都没装
- **伪包**：`firmware (>= X)`、`cy+os.ios`、`cy+lib.corefoundation`、`cy+kernel.darwin`、`gsc.*`
- **候选排序**：版本降序 → 源优先级
- **回溯 DFS**：候选不满足依赖时回退；用节点上限防爆；识别环并标注
- **版本语义**：完整 Debian 约束（`<< <= = >= >>`）与 `Provides` 的版本化语义
- **诊断**：失败时给出未解析依赖、需要者、原因（源里没有 / 版本不满足 / 目标固件不满足）

## 下载

- 每个 deb：按索引 `Filename` 拼 URL → 下载 → 校验（字节数、MD5/SHA256、deb 结构）→ 落内容寻址 pool
- 可配并发（全局并发、单源并发上限），单包失败重试，失败不连坐；任一包最终失败则整个闭包不落库
- 取消：按「无进度」判卡死，取消后清理 `.part`

## 导出

- 先按依赖关系做**拓扑排序**（依赖在前），文件名 `NN-包名_版本_架构.deb`，补零宽度随数量变化
- **只复制**：同名同内容跳过、同名不同内容不覆盖并计数；先写 `.tmp` 再改名；中断写 `manifest.partial.json`
- 导出前探测目标目录可写；单文件失败只记录并继续

## 资料库

- 下载/导出成功 = 资料库的事实：写 `archive_pool` + 一个「根插件 → 整个闭包」的分组
- 同一插件多次记录按「包名 + 版本」合并显示（可关）
- 显示列：名称（索引 `Name:`）· 版本 · 包名；支持用户自定义分组、iOS 版本筛选、列头排序、缺失文件修复

## 界面

三栏：左资料库 / 中源浏览器 / 右下载队列；顶部每源一个页签（含「全部」合并页签）。
设置项：界面语言（运行时切换）、目标设备（内置机型或自定义）、base 基线、并发与超时、资料库合并开关、源显示。

## 内置源与机型

程序内置一批源（可启用/停用、可编辑、可添加自定义源）；内置机型参数来自真机读取：
机型、iOS 版本、build、CoreFoundation 版本、Darwin 内核版本、架构。设置里可改任意字段（即自定义设备）。
"@
