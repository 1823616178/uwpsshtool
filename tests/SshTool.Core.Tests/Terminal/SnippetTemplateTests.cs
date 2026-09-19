using System.Collections.Generic;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class SnippetTemplateTests
    {
        [Fact]
        public void Render_BuiltinVariables_Replaced()
        {
            var values = new Dictionary<string, string>
            {
                { "host", "10.0.0.11" },
                { "user", "root" },
                { "port", "22" },
                { "name", "web-01" }
            };
            Assert.Equal(
                "ssh root@10.0.0.11 -p 22 # web-01",
                SnippetTemplate.Render("ssh ${user}@${host} -p ${port} # ${name}", values));
        }

        [Fact]
        public void Render_UnknownVariable_FromValues()
        {
            var values = new Dictionary<string, string> { { "svc", "nginx" } };
            Assert.Equal(
                "sudo systemctl restart nginx",
                SnippetTemplate.Render("sudo systemctl restart ${svc}", values));
        }

        [Fact]
        public void Render_MissingVariable_Empty()
        {
            Assert.Equal("a  c", SnippetTemplate.Render("a ${b} c", null));
            Assert.Equal("a  c", SnippetTemplate.Render(
                "a ${b} c", new Dictionary<string, string>()));
        }

        [Fact]
        public void Render_MultipleOccurrences_AllReplaced()
        {
            var values = new Dictionary<string, string> { { "w", "hi" } };
            Assert.Equal("hi-hi-hi", SnippetTemplate.Render("${w}-${w}-${w}", values));
        }

        [Fact]
        public void Render_Escape_DoubleDollar_ToSingleDollar()
        {
            Assert.Equal("cost $5", SnippetTemplate.Render("cost $$5", null));
            Assert.Equal("$", SnippetTemplate.Render("$$", null));
        }

        [Fact]
        public void Render_EscapedBuiltin_StaysLiteral()
        {
            var values = new Dictionary<string, string> { { "host", "x" } };
            Assert.Equal("${host}", SnippetTemplate.Render("$${host}", values));
        }

        [Fact]
        public void CollectVariables_ReturnsUnknownInOrderDistinct()
        {
            IReadOnlyList<string> names =
                SnippetTemplate.CollectVariables("${b} ${a} ${b} ${c}");
            Assert.Equal(new string[] { "b", "a", "c" }, names);
        }

        [Fact]
        public void CollectVariables_ExcludesBuiltin()
        {
            IReadOnlyList<string> names = SnippetTemplate.CollectVariables(
                "ssh ${user}@${host} -p ${port} # ${name} ${svc}");
            Assert.Equal(new string[] { "svc" }, names);
        }

        [Fact]
        public void CollectVariables_IgnoresEscaped()
        {
            IReadOnlyList<string> names =
                SnippetTemplate.CollectVariables("$${host} $${svc}");
            Assert.Empty(names);
        }

        [Fact]
        public void CollectVariables_EmptyAndNull()
        {
            Assert.Empty(SnippetTemplate.CollectVariables(null));
            Assert.Empty(SnippetTemplate.CollectVariables(string.Empty));
            Assert.Empty(SnippetTemplate.CollectVariables("no vars"));
        }

        [Fact]
        public void Render_EmptyBraces_And_Unclosed_Literal()
        {
            Assert.Equal("${}", SnippetTemplate.Render("${}", null));
            Assert.Equal("a ${b", SnippetTemplate.Render("a ${b", null));
            Assert.Equal("$", SnippetTemplate.Render("$", null));
            Assert.Equal("a $ c", SnippetTemplate.Render("a $ c", null));
        }

        [Fact]
        public void RenderWithBuiltin_BuiltinWinsOverExtra()
        {
            var builtin = new SnippetBuiltin { Host = "real", User = "u", Port = "22", Name = "n" };
            var extra = new Dictionary<string, string>
            {
                { "host", "fake" },
                { "svc", "nginx" }
            };
            Assert.Equal(
                "real nginx",
                SnippetTemplate.RenderWithBuiltin("${host} ${svc}", builtin, extra));
        }

        [Fact]
        public void RenderWithBuiltin_NullBuiltin_UsesExtra()
        {
            var extra = new Dictionary<string, string> { { "svc", "nginx" } };
            Assert.Equal("nginx", SnippetTemplate.RenderWithBuiltin("${svc}", null, extra));
            Assert.Equal(string.Empty, SnippetTemplate.RenderWithBuiltin("${host}", null, null));
        }

        [Fact]
        public void PrepareSendText_NormalizesAndAppendsEnter()
        {
            Assert.Equal("a\rb", SnippetTemplate.PrepareSendText("a\r\nb", false));
            Assert.Equal("a\rb", SnippetTemplate.PrepareSendText("a\nb", false));
            Assert.Equal("ls\r", SnippetTemplate.PrepareSendText("ls", true));
            Assert.Equal("ls\r", SnippetTemplate.PrepareSendText("ls\r", true));
            Assert.Equal("ls", SnippetTemplate.PrepareSendText("ls", false));
            Assert.Equal("\r", SnippetTemplate.PrepareSendText(null, true));
            Assert.Equal(string.Empty, SnippetTemplate.PrepareSendText(null, false));
        }

        [Fact]
        public void IsBuiltIn_CaseSensitive()
        {
            Assert.True(SnippetTemplate.IsBuiltIn("host"));
            Assert.True(SnippetTemplate.IsBuiltIn("user"));
            Assert.True(SnippetTemplate.IsBuiltIn("port"));
            Assert.True(SnippetTemplate.IsBuiltIn("name"));
            Assert.False(SnippetTemplate.IsBuiltIn("Host"));
            Assert.False(SnippetTemplate.IsBuiltIn("svc"));
            Assert.False(SnippetTemplate.IsBuiltIn(null));
        }
    }
}
