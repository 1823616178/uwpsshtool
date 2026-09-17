using System.Linq;
using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    public class AppConfigParserTests
    {
        [Fact]
        public void FullJson_AllFieldsParsed()
        {
            var cfg = AppConfigParser.Parse(
                "{\"syncApiBaseUrl\":\"http://example.com:8080\",\"allowHttp\":false,\"logLevel\":\"debug\"}");
            Assert.Equal("http://example.com:8080", cfg.SyncApiBaseUrl);
            Assert.False(cfg.AllowHttp);
            Assert.Equal("debug", cfg.LogLevel);
            Assert.Empty(cfg.Warnings);
        }

        [Fact]
        public void MissingFields_DefaultsWithWarnings()
        {
            var cfg = AppConfigParser.Parse("{}");
            Assert.Equal(AppConfigParser.DefaultSyncApiBaseUrl, cfg.SyncApiBaseUrl);
            Assert.Equal(AppConfigParser.DefaultAllowHttp, cfg.AllowHttp);
            Assert.Equal(AppConfigParser.DefaultLogLevel, cfg.LogLevel);
            Assert.Equal(3, cfg.Warnings.Count);
        }

        [Fact]
        public void InvalidJson_AllDefaultsWithWarning()
        {
            var cfg = AppConfigParser.Parse("{not json");
            Assert.Equal(AppConfigParser.DefaultSyncApiBaseUrl, cfg.SyncApiBaseUrl);
            Assert.True(cfg.AllowHttp);
            Assert.Equal("info", cfg.LogLevel);
            Assert.Single(cfg.Warnings);
        }

        [Fact]
        public void EmptyJson_AllDefaultsWithWarning()
        {
            Assert.Single(AppConfigParser.Parse("").Warnings);
            Assert.Single(AppConfigParser.Parse(null).Warnings);
        }

        [Fact]
        public void InvalidLogLevel_DefaultWithWarning()
        {
            var cfg = AppConfigParser.Parse("{\"logLevel\":\"verbose\"}");
            Assert.Equal(AppConfigParser.DefaultLogLevel, cfg.LogLevel);
            Assert.Contains(cfg.Warnings, w => w.Contains("logLevel"));
        }

        [Fact]
        public void WrongTypes_TreatedAsMissing()
        {
            var cfg = AppConfigParser.Parse("{\"syncApiBaseUrl\":42,\"allowHttp\":\"yes\"}");
            Assert.Equal(AppConfigParser.DefaultSyncApiBaseUrl, cfg.SyncApiBaseUrl);
            Assert.Equal(AppConfigParser.DefaultAllowHttp, cfg.AllowHttp);
            Assert.Equal(3, cfg.Warnings.Count); // 两个类型错 + logLevel 缺失
        }
    }
}
