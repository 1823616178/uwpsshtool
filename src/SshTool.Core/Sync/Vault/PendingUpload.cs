using System;
using Newtonsoft.Json.Linq;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Protocol;
using SshTool.Core.Storage;

namespace SshTool.Core.Sync.Vault
{
    // 03-SYNC-PROTOCOL.md §6.2 pendingUpload：未确认的文档上传事务。
    // body 是加密信封 JSON 原文（PUT sync/document 请求体逐字节一致重放，S12a 使用）；
    // document 是本次上传对应的本地文档（SameContent 判断上传期间本地是否又改动）。
    // S11 只定义编解码与持久化；重放逻辑在 S12a 落地。
    public sealed class PendingUpload
    {
        public string IdempotencyKey { get; set; }

        // PUT 时 If-Match 的基准（u64 十进制字符串，不转数字，见踩坑 #8）。
        public string BaseRevision { get; set; }

        // 加密信封 JSON 原文（act 加密输出的逐字节形态，重放时不再重新加密）。
        public string Body { get; set; }

        public SyncDocumentV1 Document { get; set; }

        public string CreatedAt { get; set; }

        public PendingUpload Clone()
        {
            return new PendingUpload
            {
                IdempotencyKey = IdempotencyKey,
                BaseRevision = BaseRevision,
                Body = Body,
                Document = Document == null ? null : Document.Clone(),
                CreatedAt = CreatedAt
            };
        }

        public JObject ToJson()
        {
            JObject document = null;
            if (Document != null)
            {
                // 经 Writer 固定键序写出后再解析，保证落盘形态与 S02 零偏差一致。
                document = JsonText.ParseObject(SyncDocumentWriter.Write(Document));
            }
            return new JObject
            {
                ["idempotencyKey"] = IdempotencyKey,
                ["baseRevision"] = BaseRevision,
                ["body"] = Body,
                ["document"] = document,
                ["createdAt"] = CreatedAt
            };
        }

        public static PendingUpload Parse(JObject o)
        {
            if (o == null)
            {
                throw new ProtocolParseException("pendingUpload", "应为对象");
            }
            var pending = new PendingUpload
            {
                IdempotencyKey = DtoReader.Str(o, "idempotencyKey", "pendingUpload.idempotencyKey"),
                BaseRevision = DtoReader.Str(o, "baseRevision", "pendingUpload.baseRevision"),
                Body = DtoReader.Str(o, "body", "pendingUpload.body"),
                CreatedAt = DtoReader.Str(o, "createdAt", "pendingUpload.createdAt")
            };
            if (string.IsNullOrEmpty(pending.IdempotencyKey)
                || string.IsNullOrEmpty(pending.BaseRevision)
                || string.IsNullOrEmpty(pending.Body))
            {
                throw new ProtocolParseException("pendingUpload", "事务字段不能为空");
            }
            var documentToken = o["document"];
            if (documentToken == null || documentToken.Type != JTokenType.Object)
            {
                throw new ProtocolParseException("pendingUpload.document", "应为对象");
            }
            // 经 S02 Reader 严格校验：云端文档 schema 零偏差同样适用于本地事务快照。
            pending.Document = SyncDocumentReader.Read(documentToken.ToString());
            return pending;
        }
    }
}
