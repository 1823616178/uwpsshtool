using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Sync.Api.Dtos
{
    // 03-SYNC-PROTOCOL.md §2.2 文档/历史分组。所有 revision 都是 u64 十进制字符串
    // （§2.1 踩坑 8：不转 double/int，避免精度损失）。

    // PUT sync/document 请求体（EncryptedDocumentEnvelope）
    public sealed class EncryptedDocumentData
    {
        public int SchemaVersion { get; set; }
        public int KeyVersion { get; set; }
        public string Algorithm { get; set; }
        public string Nonce { get; set; }
        public string Ciphertext { get; set; }
        public string CiphertextHash { get; set; }

        public JObject ToJson()
        {
            return new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["keyVersion"] = KeyVersion,
                ["algorithm"] = Algorithm,
                ["nonce"] = Nonce,
                ["ciphertext"] = Ciphertext,
                ["ciphertextHash"] = CiphertextHash
            };
        }
    }

    // GET sync/document 响应
    public sealed class SyncDocumentResponse
    {
        public string Revision { get; set; }
        public int SchemaVersion { get; set; }
        public int KeyVersion { get; set; }
        public string Algorithm { get; set; }
        public string Nonce { get; set; }
        public string Ciphertext { get; set; }
        public string CiphertextHash { get; set; }
        public string UpdatedByDeviceId { get; set; }
        public string UpdatedAt { get; set; }

        public static SyncDocumentResponse Parse(JObject o)
        {
            return new SyncDocumentResponse
            {
                Revision = DtoReader.Str(o, "revision", "revision"),
                SchemaVersion = DtoReader.Int(o, "schemaVersion", "schemaVersion"),
                KeyVersion = DtoReader.Int(o, "keyVersion", "keyVersion"),
                Algorithm = DtoReader.Str(o, "algorithm", "algorithm"),
                Nonce = DtoReader.Str(o, "nonce", "nonce"),
                Ciphertext = DtoReader.Str(o, "ciphertext", "ciphertext"),
                CiphertextHash = DtoReader.Str(o, "ciphertextHash", "ciphertextHash"),
                UpdatedByDeviceId = DtoReader.Str(o, "updatedByDeviceId", "updatedByDeviceId"),
                UpdatedAt = DtoReader.Str(o, "updatedAt", "updatedAt")
            };
        }
    }

    // PUT sync/document 响应
    public sealed class SyncWriteResponse
    {
        public string Revision { get; set; }
        public string UpdatedAt { get; set; }

        public static SyncWriteResponse Parse(JObject o)
        {
            return new SyncWriteResponse
            {
                Revision = DtoReader.Str(o, "revision", "revision"),
                UpdatedAt = DtoReader.Str(o, "updatedAt", "updatedAt")
            };
        }
    }

    public sealed class RevisionDeviceDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Platform { get; set; }
        public string AppVersion { get; set; }

        public static RevisionDeviceDto Parse(JObject o, string path)
        {
            return new RevisionDeviceDto
            {
                Id = DtoReader.Str(o, "id", path + ".id"),
                Name = DtoReader.StrOrNull(o, "name", path + ".name"),
                Platform = DtoReader.StrOrNull(o, "platform", path + ".platform"),
                AppVersion = DtoReader.StrOrNull(o, "appVersion", path + ".appVersion")
            };
        }
    }

    public sealed class RevisionItemDto
    {
        public string Revision { get; set; }
        public int SchemaVersion { get; set; }
        public int KeyVersion { get; set; }
        public string Algorithm { get; set; }
        public string CiphertextHash { get; set; }
        public RevisionDeviceDto CreatedByDevice { get; set; }
        public string CreatedAt { get; set; }

        public static RevisionItemDto Parse(JObject o, string path)
        {
            return new RevisionItemDto
            {
                Revision = DtoReader.Str(o, "revision", path + ".revision"),
                SchemaVersion = DtoReader.Int(o, "schemaVersion", path + ".schemaVersion"),
                KeyVersion = DtoReader.Int(o, "keyVersion", path + ".keyVersion"),
                Algorithm = DtoReader.Str(o, "algorithm", path + ".algorithm"),
                CiphertextHash = DtoReader.Str(o, "ciphertextHash", path + ".ciphertextHash"),
                CreatedByDevice = RevisionDeviceDto.Parse(
                    DtoReader.Obj(o, "createdByDevice", path + ".createdByDevice"), path + ".createdByDevice"),
                CreatedAt = DtoReader.Str(o, "createdAt", path + ".createdAt")
            };
        }
    }

    // GET sync/revisions 响应；pagination 结构由服务端定义，客户端原样保留
    public sealed class RevisionListResponse
    {
        public IReadOnlyList<RevisionItemDto> Items { get; set; }
        public JObject Pagination { get; set; }

        public static RevisionListResponse Parse(JObject o)
        {
            var array = DtoReader.Arr(o, "items", "items");
            var items = new List<RevisionItemDto>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                items.Add(RevisionItemDto.Parse(DtoReader.ItemObj(array[i], "items[" + i + "]"), "items[" + i + "]"));
            }
            JObject pagination = null;
            var paginationToken = o["pagination"];
            if (paginationToken != null && paginationToken.Type != JTokenType.Null)
            {
                pagination = DtoReader.ItemObj(paginationToken, "pagination");
            }
            return new RevisionListResponse { Items = items, Pagination = pagination };
        }
    }

    // DELETE sync/revisions 响应
    public sealed class DeleteRevisionsResponse
    {
        public bool Ok { get; set; }
        public string CurrentRevision { get; set; }
        public int DeletedRevisions { get; set; }

        public static DeleteRevisionsResponse Parse(JObject o)
        {
            return new DeleteRevisionsResponse
            {
                Ok = DtoReader.Bool(o, "ok", "ok"),
                CurrentRevision = DtoReader.Str(o, "currentRevision", "currentRevision"),
                DeletedRevisions = DtoReader.Int(o, "deletedRevisions", "deletedRevisions")
            };
        }
    }

    // POST sync/revisions/{revision}/restore 响应
    public sealed class RestoreRevisionResponse
    {
        public string Revision { get; set; }
        public string RestoredFromRevision { get; set; }
        public string UpdatedAt { get; set; }

        public static RestoreRevisionResponse Parse(JObject o)
        {
            return new RestoreRevisionResponse
            {
                Revision = DtoReader.Str(o, "revision", "revision"),
                RestoredFromRevision = DtoReader.Str(o, "restoredFromRevision", "restoredFromRevision"),
                UpdatedAt = DtoReader.Str(o, "updatedAt", "updatedAt")
            };
        }
    }
}
