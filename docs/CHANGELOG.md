# 交接：v2.0.0 → v3.x（v3.0.0 加四项功能，v3.0.1 / v3.0.2 各修一批已确认缺陷）

> **读法**：第〇节是 v3.0.2 的最新状态与两轮修复；第〇-1 节是 v3.0.1。
> 第一到七节记录的是 **v3.0.0 当时的设计与数字**（保留历史），其中测试项数、混音实现已按当前版本更新；
> 照着跑时一律以第〇节的命令与数字为准。

这份文件只讲**这次改了什么、为什么这么改、接手后先做什么**。功能说明、界面、协议字段表都在 `README.md`，
两者有冲突时以代码为准，并把 README 补齐。

**先看第〇节（v3.0.2）**：那是最近一次改动的清单和实测结果；第〇-1 节是 v3.0.1，下面的 v3.0.0 章节是前情。

---

## 〇、v3.0.2：第二轮审查（2026-11-24）

第二轮独立审查给了 5 个 A 类（会话/鉴权相关）和 18 个第二类缺陷，全部修完。**协议升到 v5，与 3.0.1 不互通**。

### A 类

| 编号 | 问题 | 改法 |
|---|---|---|
| A-1 | 鉴权包里设备名错位一字节：写在 48（nameLen），读却从 49 起，界面显示的名字是乱码 | 新增 `PullAuth.authName(_:)`（`Sources/PullAuth.swift`），AppModel 改走它；Windows `Proto.GetName(d, 48, d.Length)` 本就正确 |
| A-2 | 拉流中途重拉时旧令牌没换、换目标时旧连接没断：旧会话的再见能停掉新会话 | 新增 `PullSession.adopt(...)`（Mac）/`PullSessionState.Adopt(...)`（Win），**永远采用新令牌**；`acceptPull` 先换令牌再决定是否重启 |
| A-3 | 拉流请求不带密钥时（首次配对包丢了、信任表被删）只能失败重来 | 协议 v5 新增 `typePullNoKey = 9`；接收端发 `pullNoKey`（两遍）；发送端只在「刚给这个设备、这个来源 IP 发过质询」时才认，随后作废旧密钥并重新弹窗配对 |
| A-4 | 信标说设备换 IP 就无条件改目标，拉流会话会被别人带走 | 新增 `BeaconPolicy.shouldFollow(...)`（Mac/Win）：拉流会话一律不跟；同 IP 跟；不同 IP 要求 2.5 s 内没有回执 |
| A-5 | 拒绝/失败后的冷却可被「先发质询再回答」绕过，合法配对也被冷却挡住 | 有未消费质询时直接重发质询；质询过期/不匹配静默重发（不计失败）；只有 MAC 算错才计数，连续 5 次才冷却 30 秒 |

### 第二类（18 项，摘要）

`AppModel.swift:637` 的 49 改成走 `PullAuth.authName`；Windows 的 `sources` 只由网络线程改（`ReleaseSource` 改成投递请求，`Tick` 里执行；`SourceInfos`/`Stats` 同一把锁）；
采集线程 `generation` 作废旧会话、旧线程不再推数据；`requestedPid != wantPid` 每秒检查；
`Wasapi.ProcessGone` + Model 侧「目标进程不在了就回整机、并记 `CaptureNotice` 在界面显示」；
采集成功后 `Error = null`；`CaptureFallback()` 暴露回退原因；
冗余开关在包边界生效（`activeRedundancy`，原来在帧循环里每帧算帧数、包中途换长度）；
`ChallengeBook` 按「设备号 + IP」复合键、上限 64；`PullDenyList` 上限 128 且过期清理；
单路模式下 `AdmitPolicy.shouldYield` 让拉流来源优先；`switchedByPull` 在用户手动改角色/目标时清除；
被拉流结束后恢复原目标（`restoreTargetAfterPull` / `TargetMemory`）；混音时只发布主路统计；
混音目标不再 ×0.6；看门狗重启接收端后同步 `player.Mixer`；
混音回执按每路自己的缓冲与采样率；弹窗带来源 IP；选择发送端列表禁用版本不一致的设备并显示「版本不同」；
`lastPullRequestAt` / 质询表 / 拒绝表定期清理；Windows `Peer.Version` 与 `VersionMismatch`。

### 这次的实测结果

- Mac：`./build.sh` 通过；`--nettest` **113/113 全 ✓**（新增 §13「第二轮审查修复」17 项），最终验证时**连续跑 5 次全通过**；`--snapshot` 10 张布局正常。
- Windows：wine csc `/langversion:5 /warn:4` **0 error 0 warning**；`--selfcheck stage1` 18 项、`stage2` 19 项、`control` 36 项、`redundancy` 3 项、`mix` 5 项、`capture` 6 项、`stress` 2 项全过。
- 全程没跑 `--selftest`（会出声）。

### v3.0.2 仍然只有真机能验的

1. Windows 按程序采集（Process Loopback）仍未在真机执行；失败会回退并显示原因。
2. 拉流弹窗在程序只驻留菜单栏/托盘时能否被看到。
3. 两台真机之间的质询-应答、换目标先断旧连接、睡眠唤醒后自动重拉。

---


### 独立审核（第二轮）发现的阻塞项与修法

| 编号 | 问题 | 修法 |
|---|---|---|
| B1 | Windows `sources` 只加了读锁、插入/删除在锁外，UI 线程 `HasSource` 枚举时可能 `Collection was modified` | `Admit`/`Drop`/`Clear`/`FirstKey` 全部进 `lock (gate)`（`Monitor` 可重入，`Refresh` 不会自锁） |
| B2 | Windows 的 A-5 没落地：质询没消费到也回 `PullDenied` 并计失败，丢一个包就显示「对方拒绝」，5 次后连合法配对一起冷却 | `HandlePullAuth` 先查 `HasPending`，没有就静默重发质询；只有 MAC 真算错才计数；`HandlePullRequest` 在冷却判断**之前**补 `HasPending → 重发同一个 nonce` |
| S1 | `pullReply` 无条件覆盖信任密钥，同 IP 可伪造 `pullOKNew` 做密钥投毒 | 只在本机**还没有**该设备密钥时接受（改密钥必须走 revoke / NO_KEY 重配对）；两端同改 |
| S2 | `cancelPull` 不清 `pullPriorityIP`、不放行来源，被取消的机器还能把当前来源挤掉 | 取消时 `prioritize("")` + `releaseSource(pullIP)`；两端同改 |
| S5 | 用户点「允许」只清冷却、不清失败计数，刚配对的设备一次失误就进冷却 | `PullDenyList.clear` 同时清失败计数；两端同改 |
| S7 | Windows 混音模式回执按主缓冲判断「对方在放」，永远报未播放 | 改成按该路自己的 ring（`Sink(s).IsPrimed`） |
| S8 | `NetReceiver.stop()` 在 start 失败（bind 失败）时直接返回，混音器泄漏、播放端永久静音 | guard 失败分支也 `stop()` 并清空 `mixBus` |
| S9 | Windows 目标进程退出时把提示写进 `Error`，界面红字误报「无法发送」，还谎称已回退 | 不写 `Error`，只 `Plan.Decide(false, "程序已退出")`，原因走 `CaptureFallback` |
| S10 | Windows 采集失败每 0.5 s 重建会话（设备被拔/禁用时抖） | 失败次数指数退避 0.5→1→2→4 s，成功一次复位 |
| S11 | **最终验收时发现**：Mac `--nettest` 偶发 `SIGTRAP`。崩在 `monitor-audio.net` 队列上的 `deinit → stop() → queue.sync`——最后一个引用在收包回调里释放时，`stop()` 对自己的队列同步 → libdispatch 自死锁（崩溃报告两分三十二与两分四十二各一份） | `queue.setSpecific(queueKey)` + `onQueue { }`：已经在队列上就直接执行，否则 `queue.sync`；`stop()`、`hasSource()` 共三处同步点全改 |

测试也补强了：A-1 不再是「写 48 读 48」的自证（断言 48 处是名字长度、49 起是正文、改长度字节解析随之变），质询加了「重复质询必须给同一个 nonce」的两端断言。

审核给出但**未修**的（记录在案，留给真机/后续）：S3 取消时令牌在途窗口（已用本地 `releaseSource` 兜底，仍可能短暂继续出声）、S4 已被本轮加的总开关复核覆盖、S6 接收端接受无令牌再见（提示性语义，路由层不构成安全边界）、A4/A5/A6/A7 若干跨线程读（当前无崩溃证据，属加固项）、A2 Windows 用 FIFO 淘汰而 Mac 用「最早过期」近似（效果等价）、A9 `adopt(current:)` 参数未用（保留可读性，注释已说明）。

---
### 交付前整体自查（第三遍）补的四处

逐条比对 m01634 的清单与代码时发现并修掉：

| 项 | 问题 | 修法 |
|---|---|---|
| 阶段二-2 | 清单要求的「并发压力自测」没写（只有加锁，没有测试证明） | 新增 `--selfcheck stress`：收包线程持续插入来源的同时，另一线程反复 `HasSource`/`ReleaseSource` 跑 2 s，断言不抛异常；Windows 现为 7 组 selfcheck |
| 阶段二-10 | Windows 混音模式下 `Refresh(key)` 仍按「每个包的来源」发布统计，状态行在几路之间跳 | `Refresh` 开头 `if (mixEnabled) key = primaryKey;`，混音只发布主路 |
| 阶段二-14 | 接收面板 `Divider(g, 506)` 仍压在「同时接收多路」标题（y 496–518）上 | 删掉这条分隔线（上靠留白、下方 y=574 已有一条）；`--snapshot` 复检 |
| 阶段二-18 | 交接文档第一到七节仍是 3.0.0 的旧描述（36/36、5 ms 定时器混音、8/8） | 顶部加「读法」说明，并把这三处按当前实现改掉；同时把 `RequestedProcessGone()` 这段死代码删掉（检测已在采集循环里做） |

## 〇-1、v3.0.1：一批已确认缺陷的修复（2026-11-24）

一次独立代码审查给了 4 个 P0、13 个 P1、4 个 P2，全部修完。**协议升到 v4，与 3.0.0 不互通**，两端必须一起升级。

### P0（必须修）

| 编号 | 问题 | 改法 | 验证 |
|---|---|---|---|
| P0-1 | 丢包冗余少补一包：连丢 2 包时缓冲 960 帧（应 1200）、`lost 0`（应 1） | `Sources/NetReceiver.swift` 与 `windows/Net.cs` 的冗余分支改成 `missing = delta-1; conceal(missing*frames); lost += missing`，再推副本 | Mac `--nettest` §7b（gap 3/4/5 → 960/1200/1440 帧、lost 0/1/2、迟到的原包计 late）；Windows `--selfcheck redundancy` 3/3 |
| P0-2 | 混音稳态每 2 s 有 19–91 处断点；断流后最后一块 200 Hz 无限循环 | 取消 5 ms 定时器和「每路缓冲 → 主缓冲」这一层：`MixBus` 改成播放回调里逐路直接 `RingBridge.pull`（各自 inRate 重采样）再相加限幅；移除一路时 240 帧线性淡出 | Mac §8 与 Windows `--selfcheck mix`：断点 0、RMS 0.346、断流 0.5 s 后 RMS 0.00000、44.1k/48k 成分都在 |
| P0-3 | 拉流鉴权可绕过（设备号对得上就直接接受、还把长期密钥回给对方；假再见能停发送） | 协议 v4：请求 → 质询（随机数）→ `HMAC-SHA256(key, nonce‖requester‖senderID)` 截断 16 字节 → 应答（会话令牌）；密钥只在首次配对下发一次。删掉「设备号匹配就接受」旁路；再见必须 `len>=16 + 正在发送 + 来源 IP == 当前目标 + 令牌相符`；应答只认 `pullIP` 来源且只在请求中状态；Windows `NewKey` 改 `RNGCryptoServiceProvider`。新增 `Sources/PullAuth.swift`、`windows/Model.cs` 里的 `PullAuth`/`ChallengeBook`/`PullDenyList` | Mac §11 共 21 项、Windows `--selfcheck control` 里对应项（假请求/假再见/5 字节短包/假应答/重放/质询一次性/IP 绑定/错钥） |
| P0-4 | Windows 按程序采集回退后每秒重启采集（1 s 响、0.5 s 断），且界面仍显示「程序：X」 | `CapturePlan` 区分「请求 pid / 生效 pid / 是否回退」；Activate/Initialize/GetService/Start 任一步失败都回退并稳定运行；只存进程名、启动时重新解析；`AllocCoTaskMem` 与 `CoTaskMemFree` 配对、事件句柄释放、每会话 `generation` 作废旧线程；错误写 `%APPDATA%\AudioBridge\error.log` | Windows `--selfcheck capture` 6/6（决策纯函数 + 名字解析）。**WASAPI 真机行为仍未验证** |

### P1（应该修）

不截断的清单：允许拉取默认改关闭（Mac）；Windows 拉流包里的设备名改用 `d.Length`（原来永远空）；
Windows `TickPull` 只在请求中/已同意时重发；去掉无效且会误静音接收端的「发送时本机静音」开关；
内部重启（睡眠唤醒、切混音）不再给发送端发再见；链路丢失后自动重新请求（20 s 超时、等确认时 120 s）；
已有会话时换目标先给旧目标发再见并释放接收端锁定；弹窗期间回「等待对方确认」、对方放弃后点允许不生效；
被拉流结束后恢复原来的接收角色；弹窗置顶/托盘提示；选择发送端列表禁用未开启拉取的设备、手动地址失败留在弹窗；
接收界面也加「允许其它设备拉取 / 受信任设备」一行；设置与信任表改原子写入（临时文件 + `File.Replace` / `.atomic`）。

### P2（设计改进）

- 冗余包缩到 120 帧（24+480+480 = 984 字节），不再被 IP 分片；接收端按包头 `frames` 处理。
- 混音模式下主路不再每包切换（`admit` 只在增删来源时改 `primaryKey`）；拉流是否连上改用「来源表里有没有 pullIP」判断。
- Windows `SourceInfos()` 与写入方同一把锁。
- 同一 IP 错凭据限速：连续 5 次才冷却 30 秒（之前一次就封）。

### 这次的实测结果

- Mac：`./build.sh` 通过；`--nettest` **77/77 全 ✓**；`--snapshot` 10 张，接收界面新增一行后布局正常。
- Windows：wine csc `/langversion:5 /warn:4` **0 error 0 warning**；`--selfcheck control` 36 项、`redundancy` 3 项、`mix` 5 项、`capture` 6 项全过；`--snapshot` 10 张布局正常。
- 全程没跑 `--selftest`（会出声）。

### v3.0.1 仍然只有真机能验的

1. **Windows 按程序采集**：`ActivateAudioInterfaceAsync` + Process Loopback 的 COM 互操作没在真机执行过一次；
   验证方法见第二节第 4 小节。失败会回退整机并在界面/日志里说明。
2. **拉流弹窗可见性**：Mac 已 `activate`、Windows 已置顶 + 托盘气泡，但没有真人验证「只驻留菜单栏/托盘时弹窗能被看到」。
3. **两台真机之间的质询-应答**：自动化只在本机回环上验了算法、包结构与一次性/IP 绑定。
4. **睡眠唤醒后自动重新拉流**：Mac 走 `restartAll(preservePull:)` 重新 `sendPull()`；Windows 依赖自身的恢复路径。
   没有真机睡眠验证。
5. **被拉流结束后恢复接收角色**：代码路径已改，但需要一次真实的「拉流 → 对方取消」才能看到角色回退。

---

## 一、一句话总结

v3 把 README 第十节里性价比最高的四项做掉了：**接收端主动拉流（带受信任设备）、丢包冗余、多路接收混音、Windows 按程序采集**。
Mac 端全链路自动化测试通过（当时 `--nettest` 36/36；v3.0.2 为 113/113），Windows 端编译通过、界面预览通过、`--selfcheck control` 当时 8/8（v3.0.2 为 36 项）通过
（拉流控制包往返 + 受信任设备落盘），Windows↔Mac 的 v3 音频通路在 wine 里互相收通。
但两端的**真人操作**与**真机运行**都还没做。

---

## 二、改了什么

### 1. 接收端主动拉流（Mac + Windows）

以前只能跑到发送端那台电脑上，在「发送到」里选接收端。现在接收端点「选择发送端…」即可。

- 新增控制包：`type 5 拉流请求`、`type 6 拉流应答`，都走发现端口 **47832**（不新开端口），布局见 README 第六节。
- 信标第 9 字节新增 `caps`：bit0「允许拉取」、bit1「正在混音」。接收端的列表只列 `caps.bit0` 的发送端。
- 授权：发送端第一次被某台设备拉取时弹窗确认；允许后把对方的 `requesterID` 与一个随机 `key` 存进受信任表，
  以后同一个 `id + key` 直接静默接受。发送端可以整体关掉「允许拉取」，也可以逐台移除受信任设备。
- 接受后：发送端把目标切到请求方；若当前是接收角色，会自动切到发送角色（否则两台都停在接收模式，拉不动）。
- 取消：接收端往对方发现端口发 `type 4 再见`，发送端按 `currentSenderPeerID()` 比对后停发并清目标。
- 重试：请求每 1.2 s 重发一次，20 s 无应答置「拉取没有成功」；收到音频后停止重发。

信任表存储位置：

| 平台 | 路径 | 格式 |
|---|---|---|
| Mac | `~/Library/Application Support/MonitorAudioBridge/trust.json` | `{ "devices": [...], "keys": {senderID: key} }` |
| Windows | `%APPDATA%\AudioBridge\trust.ini` | 行格式 `device|id|key|name` / `key|senderId|key`（避开 System.Web 的 JSON） |

> Windows 版没有引 `System.Web.Extensions`，所以没有用 JSON；这是刻意的，为了让「拷过去就能跑」成立。

### 2. 丢包冗余（Mac + Windows）

发送端开关 `redundancy`。打开后每个音频包尾部附上**上一包**的 PCM，`packet[23] = 0x01`。
接收端按 `delta` 处理：

- `delta == 1`：正常，直接 push。
- `delta >= 2` 且包尾有副本：先把中间缺的包 conceal 掉（计 lost），再用副本把**前一包**补回去（计 `recovered`），然后 push 本包。
- 没有副本：老行为，全部 conceal。

流量约 ×2（48 kHz 立体声 16-bit → 约 3 Mbps）。代价是发送端多一次 960 字节拷贝，实测没有 CPU 压力。

**坑（两端都已踩过）**：音频头 24 字节全被占满，冗余标志借用了 tsc（单调时钟，偏移 16）的**最高字节**，
所以接收端必须按 56 位读 tsc：`tsc & 0x00FF_FFFF_FFFF_FFFF`。Mac 在 `NetReceiver.convert` 之前、
Windows 在 `NetReceiver.Handle` 里都做了这个掩码，别改回去。

### 3. 多路接收混音（Mac + Windows）

接收端开关 `mixMode`（界面「同时接收多路」），最多 4 路。

- 新增 `MixBus`：每路一个 `RingBridge`（1.5 s）。**v3.0.0 最初是 5 ms 定时器合并到主缓冲；v3.0.1 起改为播放回调里逐路 `pull`（各自 inRate 重采样）相加、±1 限幅，见第〇节 P0-2。**
- **调度不能用 `lastFillFrames`**：那个值只在 `pull` 内部更新，第一次还没拉过时是 0，会导致通道永远不拉、输出全零。
  改用新增的 `RingBridge.availableFrames`（只读，不消费）。
- 通道有 `last[]`/`hasAudio`：数据不够一个 cushion 时**复用上一块**，不推静音（否则网络抖动会变成断续空洞）。
- 混音打开时播放器固定 48 kHz（`player.inputRate = 48000`），不再跟随来源；单路模式保持 v2 行为不变。
- 单路模式下「锁定一台发送端」的语义原样保留（后来的发送端收「对方正忙」）。混音模式按 IP 最多接 4 路。

### 4. Windows 按程序采集（Win10 2004+）

`Wasapi.ActivateProcessLoopback(pid)`：`ActivateAudioInterfaceAsync("VAD\\Process_Loopback", …)` +
`AUDIOCLIENT_ACTIVATION_PARAMS{ActivationType=1, ProcessLoopbackParams{TargetProcessId, Mode=0}}`，
拿到的 `IAudioClient` 必须用**32 位浮点立体声**初始化（`AllocFloatStereoFormat`），不支持 `GetMixFormat`。

- 公开类型是必须的：`ActivateAudioInterfaceAsync` 要把托管对象当 COM 回调（CCW）传出去，
  所以 `IActivateAudioInterfaceCompletionHandler`、`IActivateAudioInterfaceAsyncOperation`、`ActivationHandler`
  都提到了命名空间级别并设为 `public`；`Wasapi` 本身仍是 `internal`。
- 失败一律回退整机采集（`wantPid = 0`），只把错误写进 `Error`/`error.log`，不会没声音。
- 界面：发送端「采集来源」下拉里新增「按程序采集…」，弹窗列出有主窗口的进程（`Process.MainWindowTitle` 非空），
  也可以一键「按设备采集」退回。

---

## 三、动手前先知道的坑

1. **协议不兼容**：v3 与 v2 不互通（全新包头语义 + 新包型）。两端必须同时更新。
2. **Win 版 `csc.exe` 只能用 C# 5**：`$""`、`?.`、`out var`、auto-property 初始化器全部不能用。
   wine 里编出来的错误行号是准的，按行改即可。
3. **wine 编译**：必须 `--bottle Steam`（CrossOver 的 wine 是 perl wrapper，`WINEPREFIX` 会报 "Unable to find the 'default' bottle"）。
   退出时 `wineserver: could not save registry branch` 是权限告警，不影响产物。
4. **`--selftest` 会出声**（volume 0.15 播 440 Hz）。改了音频路径要验证出声时，先确认用户允许；
   本次交付全程没跑 `--selftest`，只用 `--nettest`。
5. **`--nettest` 的第 6 节曾经「假失败」**：另一个 GUI 实例占着 47832，`SO_REUSEPORT` 把单播分走了。
   §6 现在给 `Discovery.listenPort` 单独设端口，已密闭；如果你又看到它失败，先 `ps aux | grep MonitorAudioBridge`。
6. **Windows 的 `TrustStore` 曾经完全不落盘**（只在内存里改，`Grant/Revoke/SetKey` 都没调 `Save()`，
   重启后受信任设备全丢、拉流要重新点一次确认）。`--selfcheck control` 抓出来的，现已修好并加了持久化断言。
7. **wine 的 loopback 只出不进**：wine 进程发出去的包能到 Mac，Mac 按「看到的源端口」回执却进不到 wine 进程
   （源端口被 wine 翻译过）。所以用 wine 对测时，Windows 侧会显示「对方无回执」——不是协议问题；
   真正要验回执，得在两台真机上跑。
8. **wine 瓶里的 `%TEMP%` 与 `%APPDATA%` 不可写**（`UnauthorizedAccessException` / 目录不存在），
   所以 Windows 自测要把临时文件放在「结果文件旁边」，不要用 `Path.GetTempPath()`。真机上没这个问题。

---

## 四、验证清单（接手后按顺序做）

### 已经自动验证的（Mac 本机，约 60 s）

```bash
cd monitor-audio-bridge
./build.sh
build/MonitorAudioBridge.app/Contents/MacOS/MonitorAudioBridge --nettest    # 期望 PASS，113 项全 ✓
```

### 已经自动验证的（Windows 端，wine 里约 20 s）

```bash
cd monitor-audio-bridge/windows
/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine --bottle Steam \
  "C:\\windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe" /nologo /codepage:65001 /target:winexe \
  /out:'Z:\\tmp\\ab-test.exe' /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll \
  Protocol.cs Ring.cs Net.cs Wasapi.cs Model.cs Ui.cs Program.cs
wine --bottle Steam 'Z:\tmp\ab-test.exe' --selfcheck control 'Z:\tmp\ctl.txt'   # 期望 8 项全 OK
```

### 需要真人 + 真机的

1. **Mac 界面**：窗口 400×720、接收/发送切换、设备下拉、缓冲档位、关窗进菜单栏、重复启动唤出窗口。
2. **拉流**（两端都要）：
   - A 机开「允许拉取」，B 机点「选择发送端…」选 A → A 弹窗 → 允许 → A 自动切到发送、B 出声；
   - B 点「取消拉取」→ A 立刻停发；
   - B 再拉一次 → **无弹窗**（受信任）；A 在「受信任设备」里移除 B → 再拉，重新弹窗；
   - A 关掉「允许拉取」→ B 拉到「对方未开启拉取」。
3. **丢包冗余**：开冗余传音乐，人为丢包（拔网线 0.2 s）听是否不再「咯」；看接收端 `recovered` 是否增长。
4. **多路混音**：两台同时发给一台（接收端开「同时接收多路」），确认两路声音都在；关掉一路，另一路不中断。
5. **Windows 真机**（最重要）：
   - 直接双击 `AudioBridge.exe`（预编译是 wine-mono 产物；打不开就 `重新编译.bat` 用系统 csc 重编）；
   - 界面在微软雅黑 + 125%/150% 缩放下的样子、托盘、防火墙弹窗、登录自启；
   - 发送：loopback 是否出声；接收：共享模式播放是否出声。
6. **Windows 按程序采集**：Win10 2004+ 上选一个正在放声音的程序（浏览器/播放器），
   确认只送这个程序的声音、通知音不送；在 Win10 1909 上确认自动回退整机采集且仍然出声。
   失败看 `%APPDATA%\AudioBridge\error.log`。

---

## 五、本次交付的取舍

| 决定 | 理由 |
|---|---|
| 拉流走发现端口，不新开端口 | 防火墙/端口映射少一处要解释；`Discovery` 已经有两端都在监听的 socket |
| 允许拉取默认**关闭** | 默认安全：不改设置的人不会被局域网里任何人拉走声音 |
| 被拉流时自动切到发送角色 | 两台都默认「接收」，不自动切就永远拉不动；弹窗文案里写明了这一点 |
| 混音与单路两条独立路径 | 单路保持 v2 行为，回归风险最小；混音的复杂度只在 `MixBus` 里 |
| Windows 用 ini 而不是 JSON | 不引 `System.Web.Extensions`，保持「系统 csc 直接编」 |
| 按程序采集失败静默回退 | 宁可按整机采集出声，也不要让用户以为程序坏了；错误进日志 |
| 冗余不做自适应开关 | 先用最简单的开关验证价值；自适应留到第十节 |

---

## 六、代码地图（只列这次动过的）

| 文件 | 改动 |
|---|---|
| `Sources/Protocol.swift` | v3 常量、`typePull/typePullReply`、`capPull/capMix`、`pull()`/`pullReply()` |
| `Sources/Discovery.swift` | `Peer.caps`、`Discovery.caps/onMessage/sendControl`、信标第 9 字节、`listenPort`（测试用） |
| `Sources/NetReceiver.swift` | 整体重写为多来源（`Source` 结构、按 IP 建表、每路独立回执/超时）、冗余补包、混音汇入、56 位 tsc |
| `Sources/NetSender.swift` | `redundancy`、`prevPayload`、包尾副本 |
| `Sources/MixBus.swift` | 新增：多路混音 |
| `Sources/RingBridge.swift` | 新增 `availableFrames` |
| `Sources/Trust.swift` | 新增：受信任设备表 |
| `Sources/AppModel.swift` | 拉流状态机、`PullState`、信任表管理、混音/冗余设置、`updateCaps`、状态文案 |
| `Sources/MainView.swift` | 接收来源段、`PullSheet`、`TrustSheet`、混音/允许拉取/冗余开关、400×720 |
| `Sources/SelfTest.swift` | `--nettest` 新增 §7 冗余、§8 混音、§9 受信任设备表、§10 拉流控制包往返；§6 改密闭端口 |
| `Sources/AppDelegate.swift` | 窗口 400×720 |
| `Resources/Info.plist` | 3.0.0 / build 3 |
| `windows/Protocol.cs` | v3 常量与封包 |
| `windows/Net.cs` | `NetReceiver` 多来源重写、`MixBus`、`SourceInfo`、发送端冗余、`Peer.caps`、`Discovery.Message/SendControl` |
| `windows/Ring.cs` | `AvailableFrames` |
| `windows/Model.cs` | `PullState`、拉流状态机、`TrustStore`、混音/冗余/按程序采集设置 |
| `windows/Ui.cs` | 接收来源、受信任设备、冗余、采集来源（按程序采集弹窗）、400×720 |
| `windows/Wasapi.cs` | Process Loopback 采集 + 失败回退 |
| `windows/Program.cs` | 版本号 3.0.0 |

---

## 七、没做完的 / 下一步建议

1. 上面「验证清单」第 2–6 项（真人、真机）。
2. 混音每路没有独立音量/静音（现在固定 1.0）。
3. 冗余不能按丢包率自适应，连续丢 2 包以上仍会 conceal。
4. Opus 编码档（1.5 Mbps → 128 kbps）没做。
5. Windows 发送端仍没有「本机静音」。

## 图标（2026-11-25 换新，v3 极简版）

- **图形**：石墨底板 + 三个形状——两根白色立柱（音箱 / 桥塔，各开一个圆孔当喇叭）和中间一道绿色下垂的弧（桥的主缆）。纯色平涂，无阴影无细节，所有尺寸同一画法。呼应菜单栏的双音箱符号，但没用 SF Symbols。
- **生成**：全部由 `scripts/make-icon.swift` 画，`./scripts/make-icon.sh` 一键重出 `Resources/AppIcon.icns` 和 `windows/app.ico`。脚本里另留两个备选（variant 1 石墨底一对音箱、2 绿底一对音箱），改 `chosen` 切换；`mkicon <目录> sheet` 出对比图，现有对比图在 `.out/icon-方案对比.png`。
- **Windows 托盘图标**：`Ui.cs` 的 `MakeIcon()` 用同一组坐标现画（32px），只有弧的颜色随状态变（灰/绿/黄）。**改图形时 make-icon.swift 和 MakeIcon() 要一起改。**
- **Mac 菜单栏图标**：没动，仍是系统符号 `hifispeaker.2`（`AppDelegate.swift`）。
- **已生效**：`windows/AudioBridge.exe` 已用新图标重编（wine csc `/langversion:5 /warn:4` 0 error 0 warning）；`build/MonitorAudioBridge.app` 里的 icns 已直接替换并重新 ad-hoc 签名（没重编代码、没动正在运行的进程）。以后正常跑 `./build.sh` 也会带上新图标。
- **未验证**：Windows 托盘图标这一版没有渲染出来看过（上一版在 wine 里看过形状，wine 的 GDI+ 不抗锯齿），真机观感未测；重编后的 exe 没重跑 `--selfcheck`（只改了 `MakeIcon()` 一个函数）。
- **坑**：换了 icns 后 Finder / Dock 可能因图标缓存仍显示旧图，重启 Finder 和 Dock（`killall Finder Dock`）或重新打开应用即可；重新签名后 macOS 可能要求重新授权「系统音频录制」。Windows 同理是资源管理器图标缓存。
- **历史版本**：`.out/icon-v1/`（最初的五根音量条：脚本、icns、ico、当时的 Ui.cs 和 exe），`.out/icon-v2/`（悬索桥细节版，用户嫌复杂弃用）。
