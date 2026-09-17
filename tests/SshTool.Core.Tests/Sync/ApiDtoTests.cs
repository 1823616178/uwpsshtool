using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Api.Dtos;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // 03-SYNC-PROTOCOL.md §10.1「每个 DTO 一条解析用例」：
    // 正常解析 + 缺必需键 → ProtocolParseException；未知键忽略；revision 全程字符串。
    public class ApiDtoTests
    {
        private static JObject Obj(string json)
        {
            return JsonText.ParseObject(json);
        }

        // ---------- 认证/账号/设备 ----------

        [Fact]
        public void AuthTokenResponse_Parses()
        {
            var dto = AuthTokenResponse.Parse(Obj(
                @"{""accessToken"":""a"",""refreshToken"":""r"",""expiresIn"":3600," +
                @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""Lumia""}," +
                @"""serverMayAdd"":""未知键忽略""}"));

            Assert.Equal("a", dto.AccessToken);
            Assert.Equal("r", dto.RefreshToken);
            Assert.Equal(3600, dto.ExpiresIn);
            Assert.Equal("u1", dto.User.Id);
            Assert.Equal("a@b.c", dto.User.Email);
            Assert.Equal("d1", dto.Device.Id);
            Assert.Equal("Lumia", dto.Device.Name);
        }

        [Theory]
        // 缺 accessToken
        [InlineData(@"{""refreshToken"":""r"",""expiresIn"":1,""user"":{""id"":""u"",""email"":""e""},""device"":{""id"":""d"",""name"":""n""}}")]
        // 缺 user
        [InlineData(@"{""accessToken"":""a"",""refreshToken"":""r"",""expiresIn"":1,""device"":{""id"":""d"",""name"":""n""}}")]
        // expiresIn 非整数
        [InlineData(@"{""accessToken"":""a"",""refreshToken"":""r"",""expiresIn"":""3600"",""user"":{""id"":""u"",""email"":""e""},""device"":{""id"":""d"",""name"":""n""}}")]
        public void AuthTokenResponse_RejectsBadShape(string json)
        {
            Assert.Throws<ProtocolParseException>(() => AuthTokenResponse.Parse(Obj(json)));
        }

        [Fact]
        public void MeResponse_Parses()
        {
            var dto = MeResponse.Parse(Obj(@"{""user"":{""id"":""u1"",""email"":""a@b.c""},""deviceId"":""d1""}"));
            Assert.Equal("u1", dto.User.Id);
            Assert.Equal("d1", dto.DeviceId);
        }

        [Fact]
        public void MeResponse_MissingDeviceId_Throws()
        {
            Assert.Throws<ProtocolParseException>(
                () => MeResponse.Parse(Obj(@"{""user"":{""id"":""u1"",""email"":""a@b.c""}}")));
        }

        [Fact]
        public void DeviceListResponse_Parses()
        {
            var dto = DeviceListResponse.Parse(Obj(
                @"{""items"":[" +
                @"{""id"":""d1"",""name"":""Lumia"",""platform"":""windows-arm"",""appVersion"":""0.1.0""," +
                @"""createdAt"":""2026-01-01T00:00:00.000Z"",""lastSeenAt"":""2026-09-17T08:00:00.000Z"",""current"":true}," +
                @"{""id"":""d2"",""name"":""PC"",""platform"":""windows-x64"",""appVersion"":""1.2.3""," +
                @"""createdAt"":""2026-02-01T00:00:00.000Z"",""lastSeenAt"":""2026-09-16T08:00:00.000Z"",""current"":false}" +
                @"]}"));

            Assert.Equal(2, dto.Items.Count);
            Assert.True(dto.Items[0].Current);
            Assert.Equal("windows-x64", dto.Items[1].Platform);
            Assert.False(dto.Items[1].Current);
        }

        [Fact]
        public void DeviceListResponse_BadItem_Throws()
        {
            Assert.Throws<ProtocolParseException>(
                () => DeviceListResponse.Parse(Obj(@"{""items"":[{""id"":""d1""}]}")));
        }

        [Fact]
        public void LogoutAllResponse_Parses()
        {
            var dto = LogoutAllResponse.Parse(Obj(@"{""revokedDevices"":3,""revokedRefreshTokens"":5}"));
            Assert.Equal(3, dto.RevokedDevices);
            Assert.Equal(5, dto.RevokedRefreshTokens);
        }

        [Fact]
        public void DeleteAccountResponse_Parses()
        {
            var dto = DeleteAccountResponse.Parse(Obj(@"{""deleted"":true,""sessionsInvalidated"":true}"));
            Assert.True(dto.Deleted);
            Assert.True(dto.SessionsInvalidated);
        }

        // ---------- 保险库 ----------

        private const string EnvelopeJson =
            @"{""keyVersion"":2,""passwordWrappedKey"":""cHc="",""passwordWrapNonce"":""cG4=""," +
            @"""recoveryWrappedKey"":""cnc="",""recoveryWrapNonce"":""cm4="",""kdfSalt"":""c2FsdA==""," +
            @"""kdfParameters"":{""algorithm"":""argon2id"",""memory"":65536,""iterations"":3,""parallelism"":4}}";

        [Fact]
        public void VaultKeyEnvelopeData_RoundTrips()
        {
            var dto = VaultKeyEnvelopeData.Parse(Obj(EnvelopeJson), "$");

            Assert.Equal(2, dto.KeyVersion);
            Assert.Equal("cHc=", dto.PasswordWrappedKey);
            Assert.Equal("argon2id", dto.KdfParameters.Algorithm);
            Assert.Equal(65536, dto.KdfParameters.Memory);
            Assert.Equal(3, dto.KdfParameters.Iterations);
            Assert.Equal(4, dto.KdfParameters.Parallelism);

            // ToJson 再 Parse 一致（请求体构造路径）
            var reparsed = VaultKeyEnvelopeData.Parse(dto.ToJson(), "$");
            Assert.Equal(dto.KeyVersion, reparsed.KeyVersion);
            Assert.Equal(dto.PasswordWrappedKey, reparsed.PasswordWrappedKey);
            Assert.Equal(dto.KdfSalt, reparsed.KdfSalt);
            Assert.Equal(dto.KdfParameters.Parallelism, reparsed.KdfParameters.Parallelism);
        }

        [Fact]
        public void VaultKeyEnvelopeData_MissingKdf_Throws()
        {
            Assert.Throws<ProtocolParseException>(() => VaultKeyEnvelopeData.Parse(Obj(
                @"{""keyVersion"":1,""passwordWrappedKey"":""a"",""passwordWrapNonce"":""b""," +
                @"""recoveryWrappedKey"":""c"",""recoveryWrapNonce"":""d"",""kdfSalt"":""e""}"), "$"));
        }

        [Fact]
        public void VaultEnvelopeResponse_Parses()
        {
            // EnvelopeJson 去掉开头 '{'，前面拼上 id 字段
            var dto = VaultEnvelopeResponse.Parse(Obj(@"{""id"":""vault-1""," + EnvelopeJson.Substring(1)));
            Assert.Equal("vault-1", dto.Id);
            Assert.Equal(2, dto.Envelope.KeyVersion);
        }

        [Fact]
        public void VaultWriteResponse_Parses()
        {
            var dto = VaultWriteResponse.Parse(Obj(@"{""id"":""vault-1"",""keyVersion"":1}"));
            Assert.Equal("vault-1", dto.Id);
            Assert.Equal(1, dto.KeyVersion);
        }

        [Fact]
        public void RotateVaultResponse_ParsesRevisionAsString()
        {
            // u64 上限值：必须原样保留为字符串（§2.1 踩坑 8）
            var dto = RotateVaultResponse.Parse(Obj(
                @"{""id"":""vault-1"",""keyVersion"":2,""revision"":""18446744073709551615"",""updatedAt"":""2026-09-17T08:00:00.000Z""}"));

            Assert.Equal("18446744073709551615", dto.Revision);
            Assert.Equal(2, dto.KeyVersion);
            Assert.Equal("2026-09-17T08:00:00.000Z", dto.UpdatedAt);
        }

        // ---------- 文档/历史 ----------

        [Fact]
        public void SyncDocumentResponse_Parses()
        {
            var dto = SyncDocumentResponse.Parse(Obj(
                @"{""revision"":""42"",""schemaVersion"":1,""keyVersion"":1,""algorithm"":""AES-256-GCM""," +
                @"""nonce"":""bg=="",""ciphertext"":""Yw=="",""ciphertextHash"":""aA==""," +
                @"""updatedByDeviceId"":""d1"",""updatedAt"":""2026-09-17T08:00:00.000Z""}"));

            Assert.Equal("42", dto.Revision);
            Assert.Equal(1, dto.SchemaVersion);
            Assert.Equal("AES-256-GCM", dto.Algorithm);
            Assert.Equal("d1", dto.UpdatedByDeviceId);
        }

        [Fact]
        public void SyncDocumentResponse_NumericRevision_Throws()
        {
            Assert.Throws<ProtocolParseException>(() => SyncDocumentResponse.Parse(Obj(
                @"{""revision"":42,""schemaVersion"":1,""keyVersion"":1,""algorithm"":""AES-256-GCM""," +
                @"""nonce"":""bg=="",""ciphertext"":""Yw=="",""ciphertextHash"":""aA==""," +
                @"""updatedByDeviceId"":""d1"",""updatedAt"":""2026-09-17T08:00:00.000Z""}")));
        }

        [Fact]
        public void EncryptedDocumentData_ToJson()
        {
            var json = new EncryptedDocumentData
            {
                SchemaVersion = 1,
                KeyVersion = 2,
                Algorithm = "AES-256-GCM",
                Nonce = "bg==",
                Ciphertext = "Yw==",
                CiphertextHash = "aA=="
            }.ToJson();

            Assert.Equal(1, (int)json["schemaVersion"]);
            Assert.Equal(2, (int)json["keyVersion"]);
            Assert.Equal("aA==", (string)json["ciphertextHash"]);
        }

        [Fact]
        public void SyncWriteResponse_Parses()
        {
            var dto = SyncWriteResponse.Parse(Obj(@"{""revision"":""13"",""updatedAt"":""2026-09-17T08:00:00.000Z""}"));
            Assert.Equal("13", dto.Revision);
            Assert.Equal("2026-09-17T08:00:00.000Z", dto.UpdatedAt);
        }

        [Fact]
        public void RevisionListResponse_ParsesWithNullableDeviceFields()
        {
            var dto = RevisionListResponse.Parse(Obj(
                @"{""items"":[" +
                @"{""revision"":""12"",""schemaVersion"":1,""keyVersion"":1,""algorithm"":""AES-256-GCM""," +
                @"""ciphertextHash"":""aA==""," +
                @"""createdByDevice"":{""id"":""d1"",""name"":""Lumia"",""platform"":""windows-arm"",""appVersion"":""0.1.0""}," +
                @"""createdAt"":""2026-09-16T08:00:00.000Z""}," +
                @"{""revision"":""11"",""schemaVersion"":1,""keyVersion"":1,""algorithm"":""AES-256-GCM""," +
                @"""ciphertextHash"":""Yg==""," +
                @"""createdByDevice"":{""id"":""d2"",""name"":null,""platform"":null,""appVersion"":null}," +
                @"""createdAt"":""2026-09-15T08:00:00.000Z""}" +
                @"],""pagination"":{""hasMore"":true,""next"":""cursor""}}"));

            Assert.Equal(2, dto.Items.Count);
            Assert.Equal("12", dto.Items[0].Revision);
            Assert.Equal("Lumia", dto.Items[0].CreatedByDevice.Name);
            Assert.Null(dto.Items[1].CreatedByDevice.Name);
            Assert.NotNull(dto.Pagination);
            Assert.True((bool)dto.Pagination["hasMore"]);
        }

        [Fact]
        public void RevisionListResponse_WithoutPagination_Parses()
        {
            var dto = RevisionListResponse.Parse(Obj(@"{""items"":[]}"));
            Assert.Empty(dto.Items);
            Assert.Null(dto.Pagination);
        }

        [Fact]
        public void RevisionListResponse_BadItem_Throws()
        {
            Assert.Throws<ProtocolParseException>(() => RevisionListResponse.Parse(Obj(
                @"{""items"":[{""revision"":""12"",""schemaVersion"":1}]}")));
        }

        [Fact]
        public void DeleteRevisionsResponse_Parses()
        {
            var dto = DeleteRevisionsResponse.Parse(Obj(
                @"{""ok"":true,""currentRevision"":""12"",""deletedRevisions"":7}"));
            Assert.True(dto.Ok);
            Assert.Equal("12", dto.CurrentRevision);
            Assert.Equal(7, dto.DeletedRevisions);
        }

        [Fact]
        public void RestoreRevisionResponse_Parses()
        {
            var dto = RestoreRevisionResponse.Parse(Obj(
                @"{""revision"":""13"",""restoredFromRevision"":""11"",""updatedAt"":""2026-09-17T08:00:00.000Z""}"));
            Assert.Equal("13", dto.Revision);
            Assert.Equal("11", dto.RestoredFromRevision);
        }

        [Fact]
        public void RestoreRevisionResponse_MissingRestoredFrom_Throws()
        {
            Assert.Throws<ProtocolParseException>(() => RestoreRevisionResponse.Parse(Obj(
                @"{""revision"":""13"",""updatedAt"":""2026-09-17T08:00:00.000Z""}")));
        }
    }
}
