using System;
using SshTool.Core.Sync;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S14：MainPage 同步图标映射（02-UI-DESIGN.md §5.1）。
    // GlyphKey/BrushKey 必须在 Themes/Tokens.xaml 中存在（App 层按当前主题解析）；
    // 此处只断言映射表本身，资源键存在性由 App 构建与画廊页保证。
    public class SyncIconMapTests
    {
        [Fact]
        public void SignedOut_MapsToCloudOffDim()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.SignedOut);
            Assert.Equal("IconCloudOff", spec.GlyphKey);
            Assert.Equal("AppTextDimBrush", spec.BrushKey);
            Assert.False(spec.Spin);
        }

        [Fact]
        public void Disabled_MapsToCloudDim()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.Disabled);
            Assert.Equal("IconCloud", spec.GlyphKey);
            Assert.Equal("AppTextDimBrush", spec.BrushKey);
            Assert.False(spec.Spin);
        }

        [Fact]
        public void Locked_MapsToLock()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.Locked);
            Assert.Equal("IconLock", spec.GlyphKey);
            Assert.Null(spec.BrushKey);
            Assert.False(spec.Spin);
        }

        [Fact]
        public void Idle_MapsToSync()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.Idle);
            Assert.Equal("IconSync", spec.GlyphKey);
            Assert.Null(spec.BrushKey);
            Assert.False(spec.Spin);
        }

        [Fact]
        public void Syncing_MapsToSpinningSync()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.Syncing);
            Assert.Equal("IconSync", spec.GlyphKey);
            Assert.True(spec.Spin);
        }

        [Fact]
        public void Synced_MapsToSyncSuccess()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.Synced);
            Assert.Equal("IconSync", spec.GlyphKey);
            Assert.Equal("AppSuccessBrush", spec.BrushKey);
            Assert.False(spec.Spin);
        }

        [Fact]
        public void Offline_MapsToCloudOffWarning()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.Offline);
            Assert.Equal("IconCloudOff", spec.GlyphKey);
            Assert.Equal("AppWarningBrush", spec.BrushKey);
            Assert.False(spec.Spin);
        }

        [Fact]
        public void Conflict_MapsToWarning()
        {
            SyncIconSpec spec = SyncIconMap.ForPhase(SyncPhase.Conflict);
            Assert.Equal("IconWarning", spec.GlyphKey);
            Assert.Equal("AppWarningBrush", spec.BrushKey);
            Assert.False(spec.Spin);
        }

        [Fact]
        public void ErrorAndAuthError_MapToSyncErrorDanger()
        {
            SyncIconSpec error = SyncIconMap.ForPhase(SyncPhase.Error);
            SyncIconSpec authError = SyncIconMap.ForPhase(SyncPhase.AuthError);
            Assert.Equal("IconSyncError", error.GlyphKey);
            Assert.Equal("AppDangerBrush", error.BrushKey);
            Assert.Equal("IconSyncError", authError.GlyphKey);
            Assert.Equal("AppDangerBrush", authError.BrushKey);
        }

        [Fact]
        public void UnknownPhase_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => SyncIconMap.ForPhase((SyncPhase)999));
        }
    }
}
