# 参考说明

本项目调用 Windows / .NET Framework 系统 API，不包含第三方库或已列项目的源码。以下项目用于研究相关实现和边界，不表示与作者存在合作或隶属关系。

- [Enable32 / Windows 10 Multi-Monitor Tray](https://github.com/Enable32/windhawk-win10-multi-monitor-tray)：MIT，研究副屏托盘、独立图标绘制和背景处理。该项目依赖 Windhawk，并面向 Windows 10 经典任务栏。
- [alzwded / AltTabber](https://github.com/alzwded/AltTabber)：BSD-2-Clause，研究多屏窗口网格、DWM 缩略图与窗口激活。
- [RitschyRigz / RitschyMirror](https://github.com/RitschyRigz/ritschy-mirror)：MIT，研究整屏及窗口画面镜像。副屏助手的桌面文件图标功能与整屏捕获不同。
- [latticelabs-au / traymirror](https://github.com/latticelabs-au/traymirror)：MIT，研究说明中的 Win11 托盘设计；2026-10-09 核查时未找到应用实现源码或正式 Release，只作为设计参考。

系统接口文档：

- [DwmRegisterThumbnail](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmregisterthumbnail)
- [IShellItemImageFactory](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishellitemimagefactory)
- [UpdateLayeredWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-updatelayeredwindow)
- [SetWindowPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos)
- [IDesktopWallpaper](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper)
- [DESKTOP_WALLPAPER_POSITION](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-desktop_wallpaper_position)

透明任务栏兼容参考 [TranslucentTB 配置说明](https://translucenttb.github.io/config.html) 中的透明与带色背景模式；未包含其代码或修改其配置。

第三方项目的许可由其作者维护；以上信息是调查快照。本项目未安装或注入 Windhawk，也未复用其 Explorer 注入代码。
