using System;
using Windows.Foundation.Metadata;

namespace SshTool.App.Platform
{
    // T10：02-UI-DESIGN.md §5.6，10 ms 轻震。VibrationDevice 是 Phone 契约，
    // 桌面 / Continuum 上类型不存在，必须 ApiInformation 守卫。
    public static class Haptics
    {
        public const int LightDurationMilliseconds = 10;

        private static readonly bool Available = ApiInformation.IsTypePresent(
            "Windows.Phone.Devices.Notification.VibrationDevice");

        public static bool IsAvailable
        {
            get { return Available; }
        }

        public static void VibrateLight(bool enabled)
        {
            if (!enabled || !Available)
            {
                return;
            }
            try
            {
                var device = Windows.Phone.Devices.Notification.VibrationDevice.GetDefault();
                if (device != null)
                {
                    device.Vibrate(TimeSpan.FromMilliseconds(LightDurationMilliseconds));
                }
            }
            catch (Exception)
            {
                // 桌面或无振动马达：静默降级。
            }
        }
    }
}
