---
name: 显示器测试报告 / Monitor report
about: 告诉我们你的显示器能不能用 / Tell us how your monitor behaves
title: "[显示器] 品牌 型号"
labels: monitor-report
---

**显示器**：品牌、型号，EDID 名（Mac：`m1ddc display list`；Windows：`ScreenSwitchCli.exe diag` 里 deviceId 中的厂商码 + 产品码）

**各输入口的编码**（VCP 0x60，在该口正显示时读到的值）：

| 输入口 | 接的设备 | 编码 |
|---|---|---|
| 例：DP | Windows | 0x10 |
| 例：USB-C | Mac | 0x0F |
| 例：HDMI 1 | 游戏机 | 0x11 |

**行为**（知道的填，不知道留空）：

- 在当前没有显示的输入口上，还能读写 DDC 吗？（能 / 不能）
- 切走输入源后，电脑还认为显示器连着吗？（是 / 否，断开连接）
- 切到没有信号的输入口时，会自动跳回有信号的口吗？大约几秒？
- 其他：读数里的垃圾值、切换延迟、休眠唤醒的问题等

**连接方式**：直连 / 扩展坞 / 转接头（型号）

**结果**：能用 / 部分能用 / 不能用，简单说明
