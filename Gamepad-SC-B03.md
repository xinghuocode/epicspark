# VR Shinecon SC-B03 蓝牙手柄 技术档案

> 本文档记录 EpicSpark 项目使用的测试手柄(VR Shinecon SC-B03)的全部技术信息、
> Linux/Unity 接入配置、Input System 映射表及固件缺陷。最后更新:2026-11-06。

## 1. 设备概述

| 项目 | 内容 |
|---|---|
| 品牌 | VR Shinecon(千幻魔镜) |
| 型号 | SC-B03(迷你蓝牙手柄 / VR 遥控器) |
| 定位 | 五合一:游戏手柄 / VR 遥控 / 自拍快门 / 无线鼠标 / 多媒体遥控 |
| 蓝牙 | 3.0(BLE 模式连接),距离 2-10m |
| 供电 | 2 节 AAA(7号)电池,非充电 |
| 续航 | 待机 40h+,连续使用约 36-40h |
| 按键 | 1×360° 摇杆 + A/B/X/Y + 确认键 + 模式键 + 电源键 |
| 主控 | 珠海杰理(JieLi)BLE 芯片,ARM968E-S 内核 |
| 尺寸/重量 | 约 110×43×37mm / 150g |
| 参考价格 | 约 20-25 元人民币 |

## 2. 蓝牙 / HID 信息

| 项目 | 值 |
|---|---|
| 设备名 | `SC-B03-5EAF`(后缀因设备而异) |
| MAC | `FF:FF:FF:37:26:65` |
| 配对名模式 | `SC-B03-XXXX` |
| 工作模式 | HID(游戏手柄模式) |
| 伪装身份 | `usb:v05ACp022Cd011B`(冒用 Apple 厂商 ID,为兼容 iOS iCade 模式) |
| 私有 GATT 服务 | `0xAE40`(可写特征 `0xAE41` + 通知特征 `0xAE42`)——实测为死通道,无协议回应 |

### HID 报告描述符(4 个 Report)

| Report ID | 类型 | 内容 |
|---|---|---|
| 0x01 | Consumer | 多媒体键(播放/暂停、音量±、上一首/下一首) |
| 0x02 | Gamepad | 16 按钮 + 摇杆 X/Y + 十字键 |
| 0x03 | Keyboard | 仅 8 个修饰键 + 系统键(无普通按键数组) |
| 0x04 | Mouse | 3 键 + X/Y/滚轮(对应其"鼠标模式") |

## 3. Linux 接入配置(必须)

该设备是"键盘+鼠标+摇杆"三合一 HID,udev 默认**不会**给它打 `ID_INPUT_JOYSTICK`
标签,导致 SDL(以及 Unity Linux 版)完全看不到这个手柄。

### udev 规则(已安装)

文件:`/etc/udev/rules.d/99-shinecon-joystick.rules`

```udev
SUBSYSTEM=="input", ATTRS{name}=="SC-B03-*", MODE="0666", ENV{ID_INPUT_JOYSTICK}="1"
```

- `ENV{ID_INPUT_JOYSTICK}="1"`:强制摇杆标签(SDL 识别的必要条件)
- `MODE="0666"`:放开设备节点权限
- 注意:**蓝牙设备必须重启电脑才生效**(udevadm trigger 对蓝牙 HID 不可靠)
- 规则用 `SC-B03-*` 通配,重连、换电池均自动覆盖

### 验证

```bash
udevadm info --query=property --name=/dev/input/js0 | grep JOYSTICK
# 期望输出:ID_INPUT_JOYSTICK=1,且 js0 权限为 crw-rw-rw-
```

### 系统设备节点

| 节点 | 说明 |
|---|---|
| `/dev/input/js0` | 摇杆(joystick)接口 |
| `/dev/input/event256` | evdev 接口(键盘+鼠标+摇杆事件合并) |
| `/dev/hidraw6` | 原始 HID(需 root) |

### 使用注意

- **先开手柄,再开 Unity**(Linux 版 legacy 输入不支持摇杆热插拔)
- 电量上报恒为 0%(干电池设备,固件不上报真实电量,可忽略)

## 4. Unity 配置

| 项目 | 值 |
|---|---|
| Input System 包 | `com.unity.inputsystem 1.7.0` |
| Active Input Handling | **Both**(新旧输入并存) |
| 控制器脚本 | `Assets/Scripts/MovementTest/InputSystemPlayerController.cs` |
| 场景 | `Assets/Scenes/MovementTest.unity` |
| 调试面板 | 运行时左上角,可切换轴映射、枚举显示全部按钮状态 |

键位:键盘 WASD/方向键移动、空格跳跃、左Shift加速;手柄摇杆移动、A跳跃、确定键加速。

## 5. Input System 按钮映射表(实测校准)

以 `InputSystemPlayerController` 枚举顺序(按 control path 排序)为准:

| 物理键 | 枚举索引 | 备注 |
|---|---|---|
| **A(跳跃)** | **19** | |
| **B** | **20** | 仅在摇杆活动时上报(见固件缺陷) |
| **X** | **16** | |
| **Y** | **15 或 21**(任一触发) | 一个物理键映射两个控件,检测时任一命中即算按下 |
| **确定键** | **17** | 十字键下方的小功能键,用作加速 |

Inspector 默认值:Jump Button Index = 19,Sprint Button Index = 17,Y Button Indices = {15, 21}。

### 默认轴映射

该手柄固件轴序反置 + Y 轴反向,控制器默认勾选:

- ✅ 交换 X/Y 轴(swapStickXY = true)
- ✅ 反转 Y 轴(invertY = true)
- ⬜ 反转 X 轴(invertX = false)

## 6. 固件缺陷清单(重要)

### 6.1 按键"锁存捎带"上报
摇杆静止时按任何键,固件**不发送任何 HID 报告**;按键状态被锁存,直到下次摇杆
活动才随游戏报告"搭车"发出。表现为:静止时按键毫无反应,一动摇杆按键状态才冒出。
→ 游戏中依赖手柄按键时需知晓此延迟特性,无法从软件层修复。

### 6.2 B 键不在游戏报告里
B 键(返回/音量-)经**键盘/Consumer 通道**发送,不出现在 Joystick/Gamepad 设备的
控件枚举中(映射表中 B=20 是摇杆活动时的实际表现,静止时拿不到)。

### 6.3 摇杆 X/Y 轴反置
固件上报的轴序与直觉相反(表现为左右/前后颠倒),需在控制器中勾"交换 X/Y 轴"。
此为固件行为,与 Linux/Unity 无关(手机端 App 无感是因为它们内置了适配)。

### 6.4 Y 键双控件
按 Y 会同时触发枚举索引 15 和 21 两个控件。

### 6.5 私有 GATT 命令通道不可用
`0xAE41` 可写但无任何协议回应,无法从电脑端切换手柄模式(模式只能按物理组合键)。

## 7. 手柄物理操作备忘

| 操作 | 方法 |
|---|---|
| 开机 | 电源键按住约 3 秒 |
| 关机 | 电源键按住约 5 秒 |
| 模式切换 | 模式键 + A=鼠标 / +B=多媒体 / +X=游戏 / 模式键长按 5 秒=回 VR 模式 |
| 自动关机 | 无连接 5 分钟 / 无操作 30 分钟 |
| 连接方式 | 开机自动回连上次设备;新设备搜索 `SC-B03-XXXX` 配对 |
