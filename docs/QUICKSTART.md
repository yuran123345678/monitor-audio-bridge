# 音频桥 v3.0.2 · 交付说明

> 这份是「拿到东西先怎么用」和「哪些还没验证」的短文档。
> 设计取舍、每一项改在哪里、踩过的坑，在 `docs/CHANGELOG.md`；完整说明在 `README.md`。

---

## 一、现在是什么状态

| | 状态 |
|---|---|
| 版本 | **v3.0.2**（协议 **v5**，与 3.0.1 及更早 **不互通**，两端要一起换） |
| Mac | 已编译（`build/MonitorAudioBridge.app`）；`--nettest` **113/113 通过** |
| Windows | 已用 CrossOver 的 csc（C# 5、`/warn:4`）编译，**0 错误 0 警告**；`--selfcheck` 六组全过；**没在真 Windows 上跑过** |
| 界面 | 两端 `--snapshot` 出图逐张看过，布局正常；**没有真人点击验证** |

一句话：**逻辑和自动化测试是通的，真机（尤其是 Windows 和真实音频设备）仍需现场验一遍。**

---

## 二、两分钟上手

### Mac → Windows（Windows 收）

1. 两台机器连同一个 Wi-Fi/局域网。
2. Windows 双击 `windows/AudioBridge.exe`，角色选「接收」，点「开始接收」。
3. Mac 打开 `build/MonitorAudioBridge.app`，角色选「发送」，在「发送到」里点那台 Windows，点「开始发送」。
4. Windows 上出现电平、Mac 状态卡显示「对方正在播放」即通。

### 反过来（Mac 收）

Windows 选「发送」并选目标，Mac 选「接收」→「选择发送端…」点 Windows（也可以直接点「开始接收」等对方推）。

### 用拉流（接收端主动要）

1. 发送端（被要声音的那台）确认「允许其它设备拉取」是开着的。
2. 接收端点「选择发送端…」，选一台设备，或手动输入 IP。
3. **第一次**：发送端弹窗「XX 请求接收这台电脑的声音」→ 点「允许」。之后这台设备被记为受信任设备，不再弹。
4. 接收端想停：点「取消拉取」。发送端那边声音会停，发送端回到接收角色（如果是被拉流切过去的）。

发送端管理受信任设备：「受信任设备 · N 台」→ 可以把某台移除，或「全部移除」（下次要重新确认）。

---

## 三、验证命令（都在项目根目录）

```bash
# Mac：编译 + 自动化测试（不发声音，可以随时跑）
./build.sh
build/MonitorAudioBridge.app/Contents/MacOS/MonitorAudioBridge --nettest      # 应输出 PASS，113/113

# Mac：只看界面（每个状态存一张 PNG，不发声）
build/MonitorAudioBridge.app/Contents/MacOS/MonitorAudioBridge --snapshot /tmp/shots

# Windows：编译（Mac 上用 CrossOver 的 wine + .NET 4 的 csc）
cd windows
/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine --bottle Steam \
  "C:\\windows\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe" /nologo /codepage:65001 \
  /langversion:5 /warn:4 /target:winexe /out:'Z:\tmp\ab-test.exe' \
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll \
  Protocol.cs Ring.cs Net.cs Wasapi.cs Model.cs Ui.cs Program.cs

# Windows：自测（结果写到指定文件）
'Z:\tmp\ab-test.exe' --selfcheck stage1   'Z:\tmp\s1.txt'
'Z:\tmp\ab-test.exe' --selfcheck stage2   'Z:\tmp\s2.txt'
'Z:\tmp\ab-test.exe' --selfcheck control  'Z:\tmp\c.txt'
'Z:\tmp\ab-test.exe' --selfcheck redundancy 'Z:\tmp\r.txt'
'Z:\tmp\ab-test.exe' --selfcheck mix      'Z:\tmp\m.txt'
'Z:\tmp\ab-test.exe' --selfcheck capture  'Z:\tmp\cap.txt'
'Z:\tmp\ab-test.exe' --selfcheck stress   'Z:\tmp\st.txt'
```

**不要跑 `--selftest`**：它会真的以 15% 音量播 440 Hz（会出声）。

最近一次实跑结果：Mac `--nettest` **113/113 PASS，连续 5 次全通过**；Windows `stage1` 18、`stage2` 19、`control` 36、`redundancy` 3、`mix` 5、`capture` 6、`stress` 2，全部 0 失败。

---

## 四、这轮（3.0.1 → 3.0.2）修了什么

两轮修复，细节在 `docs/CHANGELOG.md` 第〇节。摘要：

**第一轮（A/B 类）**

- **A-1** Windows 确认弹窗每次都抛异常（传了 `DefaultDesktopOnly` + 所属窗口）→ 首次配对做不了。去掉了该选项，异常按「拒绝」处理并记日志，单个控制包出错不再影响同一拍其它包。
- **A-2** 重新拉流后「取消」失效：发送端提前 return 没更新会话令牌。两端统一成「先存新令牌再返回」。
- **A-3** 首次配对的应答包丢一个就卡死：新增协议包 **NO_KEY**（type 9）——接收端没密钥就明确说，发送端作废旧密钥、退回弹窗重新配对；拉流应答与再见各连发 3 遍，接收端幂等。
- **A-4** 伪造信标能把声音引走：有会话令牌时绝不跟信标换地址；普通推送只在旧地址 2.5 s 没回执时才跟。
- **A-5** Windows 睡眠唤醒仍会取消拉流：移植 Mac 的 `ApplyRunState(preservePull:)`，内部重启不发再见，起来自动重新请求。

**第二轮（18 项）**

- Mac 读鉴权包设备名偏移错一字节（48/49）——改成真实路径上的 `PullAuth.authName`，测试也改成走真实函数。
- Windows 来源表加锁（`sources` 只归网络线程改）、采集线程代数、`RequestedProcessGone` 接线、回退状态单独显示、采集失败指数退避。
- 冗余开关切换的竞态（包边界切换 `activeRedundancy`，`filled >= per`，副本长度 = 当前包帧数×4）。
- 质询表按「设备号 + IP」存、重复质询复用 nonce、只有 MAC 算错才计失败（避免一丢包就显示「对方拒绝」）。
- 被拉流时单路让位、`switchedByPull` 清理与恢复用户原目标、混音统计只发主路、看门狗重启后同步 `mixer`。
- 版本门禁（版本不同的设备列出但标注「不能互连」）、过期表清理、混音回执按来源自己的缓冲、文档与注释修正。

**最终验收时又抓到一处**：`--nettest` 偶发 `SIGTRAP`（收包回调里释放接收器 → `deinit` 对自己的队列 `queue.sync` 自死锁）。已改为「已在队列上就直接执行」，之后连续跑 5 次全过。

**独立审核**又抓到两个阻塞项，也修了：Windows `sources` 并发修改（`Collection was modified`）、质询没消费到被当成失败并冷却；另修 7 项（密钥投毒、取消时不释放来源/优先级、`clear` 不清失败计数、混音回执读错缓冲、`stop()` 泄漏混音器、进程退出误报、采集无退避）。

---

## 五、已知限制 / 没验证的事（务必现场确认）

1. **Windows 按程序采集（Process Loopback）一次都没在真 Windows 上执行过。** 这是纯手写的 COM 互操作；失败时会回退整机采集并在界面显示「整机（按程序采集不可用）」，原因写 `%APPDATA%\AudioBridge\error.log`。
2. **拉流确认弹窗**在程序只驻留菜单栏/托盘、没有主窗口时是否真的弹到前台（已加置顶 + 托盘气泡兜底，未真机验证）。
3. **跨机质询-应答**只在同一台机器（wine ↔ Mac）的端口上验证过，两台真机没试过。
4. **取消拉流的瞬间**，如果「取消」包和音频包在路上交错，可能还会响一声（已在本地释放来源兜底）。
5. 睡眠唤醒自动重拉、被拉流结束后恢复用户原来的推送目标：有单元测试，但没在真机上走完整流程。
6. Mac 界面没有真人点击验证；系统声音采集需要「屏幕录制/音频录制」授权后才有真实数据（未授权时是静音）。
7. 音频是**明文** UDP，只在可信局域网里用；启用「丢包冗余」后流量约 ×2。

---

## 六、兼容与回滚

- 协议 **v5**。两端必须一起升；混用 3.0.1/3.0.2 会连不上（界面会显示「版本不同」）。
- 设置与信任关系位置：
  - Mac：`~/Library/Application Support/MonitorAudioBridge/`（`settings`、`trust.json`、`instance.lock`）
  - Windows：`%APPDATA%\AudioBridge\`（`settings.ini`、`trust.ini`、`error.log`）
- 回滚：把两端的程序文件换回 3.0.1 构建即可（协议 v4 与 v5 不互通，同样要两端一起换）。信任文件格式兼容，不用删。
