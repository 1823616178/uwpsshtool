using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;

namespace SshTool.App.ViewModels.Snippets
{
    // U13：片段发送统一入口（终端菜单 / 键条 snippets 键 / 右键菜单 / 管理页发送共用）。
    //
    // 流程：内置变量由会话填充 → 待填变量弹 SnippetVariableDialog →
    // 渲染 → 换行归一 + SendEnter 追加回车 → UTF-8 编码 → ISshSession.Write。
    // 日志脱敏：只记片段名称与文本长度，绝不记内容明文。
    public static class SnippetSendHelper
    {
        public static SnippetBuiltin BuiltinFromSession(SessionInfo session)
        {
            var builtin = new SnippetBuiltin();
            if (session == null)
            {
                return builtin;
            }
            builtin.Host = session.HostName ?? string.Empty;
            builtin.User = session.Username ?? string.Empty;
            builtin.Port = session.Port.ToString(CultureInfo.InvariantCulture);
            builtin.Name = session.Title ?? string.Empty;
            return builtin;
        }

        // 完整发送（含弹窗）。取消或无可用会话返回 false。
        public static async Task<bool> SendWithPromptsAsync(
            Snippet snippet, SessionInfo session, ILogger logger)
        {
            if (snippet == null)
            {
                return false;
            }
            ISshSession native = session == null ? null : session.NativeSession;
            if (native == null)
            {
                if (logger != null)
                {
                    logger.Log(LogLevel.Warning, "Snippet",
                        "发送片段失败：会话不可用 name=\"" + (snippet.Name ?? string.Empty) + "\"");
                }
                return false;
            }
            SnippetBuiltin builtin = BuiltinFromSession(session);
            IReadOnlyList<string> variables =
                SnippetTemplate.CollectVariables(snippet.Content);
            IDictionary<string, string> values = null;
            if (variables.Count > 0)
            {
                SnippetVariableResult collected = await SnippetVariableDialog.ShowAsync(
                    snippet.Name, snippet.Content, builtin, variables).ConfigureAwait(true);
                if (collected == null || !collected.Confirmed)
                {
                    return false;
                }
                values = collected.Values;
            }
            string rendered = SnippetTemplate.RenderWithBuiltin(snippet.Content, builtin, values);
            SendRendered(rendered, snippet, native, logger);
            return true;
        }

        // 无变量直发（调用方已保证无需弹窗；否则仍按空值渲染）。
        public static void SendDirect(
            Snippet snippet, SessionInfo session, ILogger logger)
        {
            if (snippet == null)
            {
                return;
            }
            ISshSession native = session == null ? null : session.NativeSession;
            if (native == null)
            {
                return;
            }
            string rendered = SnippetTemplate.RenderWithBuiltin(
                snippet.Content, BuiltinFromSession(session), null);
            SendRendered(rendered, snippet, native, logger);
        }

        private static void SendRendered(
            string rendered, Snippet snippet, ISshSession native, ILogger logger)
        {
            string sendText = SnippetTemplate.PrepareSendText(
                rendered, snippet.SendEnter);
            byte[] data = Encoding.UTF8.GetBytes(sendText);
            try
            {
                native.Write(data);
            }
            catch (Exception ex)
            {
                if (logger != null)
                {
                    logger.Log(LogLevel.Error, "Snippet",
                        "发送片段异常 name=\"" + (snippet.Name ?? string.Empty)
                        + "\" error=" + ex.GetType().Name);
                }
                return;
            }
            if (logger != null)
            {
                logger.Log(LogLevel.Info, "Snippet",
                    "发送片段 name=\"" + (snippet.Name ?? string.Empty)
                    + "\" len=" + sendText.Length.ToString(CultureInfo.InvariantCulture)
                    + " enter=" + (snippet.SendEnter ? "1" : "0"));
            }
        }
    }
}
