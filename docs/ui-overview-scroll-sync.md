# 首页滚动条位置同步

首页 ResponsiveViewport 为支持图表和卡片上的滚轮输入，会通过消息路由直接设置原生内层 ScrollableControl.AutoScrollPosition。AntdUI.Panel 通常在内层 WM_MOUSEWHEEL / WM_PAINT 等消息中加载官方滚动条位置，程序设置滚动位置可能没有触发该路径，导致内容已移动而滑块停留在原处。

GatewayScrollPanel 在滚动位置、滚动范围和布局变化后，使用官方 YScrollBar / XScrollBar.LoadValue 读取内层真实滚动状态。该 API 不触发 ValueChanged，另保留重入保护，避免滑块与内容互相推送。继续使用 AntdUI 默认滚动条，不自绘、不替换皮肤。

经用户授权，本轮 Release 编译通过：0 错误，420 项既有驱动/Worker 警告。隔离后台 OverviewScroll 专项通过：滚轮到底/向上、实际滑块拖动、滑块值与内容偏移一致、实时快照刷新保持、菜单展开/折叠、共用虚拟点位选择列表滚动和勾选保持。退出码 0、stderr 为空。实际 DPI 192，已复核底部截图，没有修改桌面缩放。

证据：`artifacts/overview-scroll-build.log`、`artifacts/overview-scroll-ui`。本轮未运行全量核心、八页面全量滚动、真实设备或真实跨屏 DPI 验收。
