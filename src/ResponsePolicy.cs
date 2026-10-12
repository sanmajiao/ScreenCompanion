using System;
namespace ScreenCompanion
{
    internal static class ResponsePolicy
    {
        internal static bool High { get { return Settings.Current!=null && Settings.Current.HighResponse; } }
        internal static int DesktopRefresh { get { return High?80:350; } }
        internal static int DesktopHeartbeat { get { return High?750:2000; } }
        internal static int WindowActivation { get { return High?25:90; } }
        internal static int WindowGuard { get { return High?32:100; } }
        internal static int TrayPoll { get { return High?16:32; } }
        internal static int TrayFrame { get { return High?40:120; } }
        internal static int StartDiscovery { get { return High?16:25; } }
        internal static int PopupTracking { get { return High?25:50; } }
        internal static long ImageCacheLimit { get { return High?16L*1024*1024:0; } }
    }
}
