using System;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Profile;

namespace SshTool.App.Platform
{
    // S08：IDeviceDescriptorProvider 的 UWP 实现。
    // 名称默认 EasClientDeviceInformation.FriendlyName（≤255，取不到时回退 "Lumia"）；
    // 平台：手机上 "windows-mobile-arm"，其他按包架构 "windows-uwp-<arch>"；
    // 版本取包版本 Major.Minor.Build.Revision（03-SYNC-PROTOCOL.md §7.1）。
    // 本文件用到的均为 TargetPlatformMinVersion(15063) 基线 API；
    // EAS / Analytics 的存在性仍用 ApiInformation 守卫，缺失时走回退值，
    // 保证在任何 SKU 上都不抛异常。
    public sealed class UwpDeviceDescriptorProvider : IDeviceDescriptorProvider
    {
        public const int MaxNameLength = 255;

        public DeviceDescriptorData GetDescriptor(string nameOverride = null)
        {
            string name = string.IsNullOrWhiteSpace(nameOverride)
                ? DefaultDeviceName()
                : nameOverride.Trim();
            if (name.Length > MaxNameLength)
            {
                name = name.Substring(0, MaxNameLength);
            }
            if (name.Length == 0)
            {
                name = "Lumia";
            }
            return new DeviceDescriptorData
            {
                Name = name,
                Platform = CurrentPlatform(),
                AppVersion = CurrentAppVersion()
            };
        }

        internal static string DefaultDeviceName()
        {
            try
            {
                if (!ApiInformation.IsTypePresent(
                    "Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation"))
                {
                    return "Lumia";
                }
                var info = new EasClientDeviceInformation();
                string friendly = info.FriendlyName;
                if (!string.IsNullOrWhiteSpace(friendly))
                {
                    return friendly.Trim();
                }
            }
            catch (Exception)
            {
                // 取不到设备名不影响登录：回退固定名。
            }
            return "Lumia";
        }

        internal static string CurrentPlatform()
        {
            try
            {
                if (ApiInformation.IsTypePresent("Windows.System.Profile.AnalyticsInfo"))
                {
                    string family = AnalyticsInfo.VersionInfo.DeviceFamily;
                    if (string.Equals(family, "Windows.Mobile", StringComparison.OrdinalIgnoreCase))
                    {
                        return "windows-mobile-arm";
                    }
                }
            }
            catch (Exception)
            {
                // 降级走架构分支。
            }
            string arch = "unknown";
            try
            {
                arch = Package.Current.Id.Architecture.ToString().ToLowerInvariant();
            }
            catch (Exception)
            {
            }
            return "windows-uwp-" + arch;
        }

        internal static string CurrentAppVersion()
        {
            try
            {
                var version = Package.Current.Id.Version;
                return version.Major.ToString() + "." + version.Minor.ToString() + "."
                    + version.Build.ToString() + "." + version.Revision.ToString();
            }
            catch (Exception)
            {
                return "0.0.0.0";
            }
        }
    }
}
