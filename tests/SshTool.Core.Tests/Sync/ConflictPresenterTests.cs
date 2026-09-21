using System.Collections.Generic;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // U19：ConflictPresenter 单测 —— 名称查找顺序 + 敏感字段不含实际值 + 三种 reason 文案键。
    public class ConflictPresenterTests
    {
        private readonly ConflictPresenter _presenter = new ConflictPresenter();

        // 伪查找：本机名命中返回本机名，否则按 entity+id 拼远端 title。
        private class FakeLookup : IEntityNameLookup
        {
            private readonly Dictionary<string, string> _locals;
            public FakeLookup(Dictionary<string, string> locals) { _locals = locals; }
            public string LocalName(SyncConflictEntity entity, string id)
            {
                string key = entity + ":" + id;
                return _locals != null && _locals.ContainsKey(key) ? _locals[key] : null;
            }
            public string RemoteTitle(SyncConflictEntity entity, string id)
            {
                return "remote-" + entity + "-" + id;
            }
        }

        // ---- 三种 reason 文案键非空 ----

        [Fact]
        public void ResolveTitle_AllReasons_NonEmpty()
        {
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveTitle(SyncConflictReason.InitialImport)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveTitle(SyncConflictReason.RemoteDeletion)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveTitle(SyncConflictReason.MergeConflict)));
        }

        [Fact]
        public void ResolveDescription_AllReasons_NonEmpty()
        {
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveDescription(SyncConflictReason.InitialImport)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveDescription(SyncConflictReason.RemoteDeletion)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveDescription(SyncConflictReason.MergeConflict)));
        }

        [Fact]
        public void ResolvePrimaryButton_AllReasons_NonEmpty()
        {
            Assert.False(string.IsNullOrEmpty(_presenter.ResolvePrimaryButton(SyncConflictReason.InitialImport)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolvePrimaryButton(SyncConflictReason.RemoteDeletion)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolvePrimaryButton(SyncConflictReason.MergeConflict)));
        }

        [Fact]
        public void ResolveSecondaryButton_AllReasons_NonEmpty()
        {
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveSecondaryButton(SyncConflictReason.InitialImport)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveSecondaryButton(SyncConflictReason.RemoteDeletion)));
            Assert.False(string.IsNullOrEmpty(_presenter.ResolveSecondaryButton(SyncConflictReason.MergeConflict)));
        }

        [Fact]
        public void ResolveButtons_DifferByReason()
        {
            // remote-deletion 主按钮文案不同于 merge-conflict。
            Assert.NotEqual(
                _presenter.ResolvePrimaryButton(SyncConflictReason.RemoteDeletion),
                _presenter.ResolvePrimaryButton(SyncConflictReason.MergeConflict));
        }

        // ---- 名称查找顺序：本机名 → 远端 title → id ----

        [Fact]
        public void ResolveFieldEntityName_LocalHit_ReturnsLocalName()
        {
            var field = new SyncConflictField
            {
                Entity = SyncConflictEntity.Server,
                Id = "srv-1",
                Field = "profile.port",
                Sensitive = false
            };
            var lookup = new FakeLookup(new Dictionary<string, string> { { "Server:srv-1", "web-01" } });
            Assert.Equal("web-01", _presenter.ResolveFieldEntityName(field, lookup));
        }

        [Fact]
        public void ResolveFieldEntityName_NoLocal_ReturnsRemoteTitle()
        {
            var field = new SyncConflictField
            {
                Entity = SyncConflictEntity.Server,
                Id = "srv-2",
                Field = "profile.port",
                Sensitive = false
            };
            var lookup = new FakeLookup(new Dictionary<string, string>());
            Assert.Equal("remote-Server-srv-2", _presenter.ResolveFieldEntityName(field, lookup));
        }

        [Fact]
        public void ResolveFieldEntityName_NullLookup_ReturnsId()
        {
            var field = new SyncConflictField
            {
                Entity = SyncConflictEntity.Tunnel,
                Id = "tun-9",
                Field = "*",
                Sensitive = false
            };
            Assert.Equal("tun-9", _presenter.ResolveFieldEntityName(field, null));
        }

        [Fact]
        public void ResolveFieldEntityName_NullField_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, _presenter.ResolveFieldEntityName(null, new FakeLookup(null)));
        }

        // ---- 敏感字段不含实际值 ----

        [Fact]
        public void ResolveFieldLabel_Sensitive_UsesSensitiveKey()
        {
            var field = new SyncConflictField
            {
                Entity = SyncConflictEntity.Server,
                Id = "srv-1",
                Field = "secrets.password",
                Sensitive = true
            };
            Assert.Equal("Sync_Conflict_SensitiveChanged", _presenter.ResolveFieldLabel(field));
        }

        [Fact]
        public void ResolveFieldLabel_NonSensitive_UsesFieldName()
        {
            var field = new SyncConflictField
            {
                Entity = SyncConflictEntity.Server,
                Id = "srv-1",
                Field = "profile.port",
                Sensitive = false
            };
            Assert.Equal("profile.port", _presenter.ResolveFieldLabel(field));
        }

        [Fact]
        public void FieldDisplayNames_SensitiveField_DoesNotContainFieldValue()
        {
            var fields = new List<SyncConflictField>
            {
                new SyncConflictField
                {
                    Entity = SyncConflictEntity.Server,
                    Id = "srv-1",
                    Field = "secrets.password",
                    Sensitive = true
                }
            };
            var lookup = new FakeLookup(new Dictionary<string, string> { { "Server:srv-1", "db-01" } });
            var specs = _presenter.FieldDisplayNames(fields, lookup);
            Assert.Single(specs);
            Assert.Equal("db-01", specs[0].EntityName);
            Assert.Equal("Sync_Conflict_SensitiveChanged", specs[0].FieldLabel);
            // 绝不包含敏感字段名本身。
            Assert.NotEqual("secrets.password", specs[0].FieldLabel);
        }

        [Fact]
        public void FieldDisplayNames_NullFields_ReturnsEmpty()
        {
            Assert.Empty(_presenter.FieldDisplayNames(null, new FakeLookup(null)));
        }

        [Fact]
        public void FieldDisplayNames_NullFieldEntry_Skipped()
        {
            var fields = new List<SyncConflictField> { null };
            var specs = _presenter.FieldDisplayNames(fields, new FakeLookup(null));
            Assert.Empty(specs);
        }
    }
}
