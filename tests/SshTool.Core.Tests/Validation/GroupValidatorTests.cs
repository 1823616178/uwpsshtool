using SshTool.Core.Models;
using SshTool.Core.Validation;
using Xunit;

namespace SshTool.Core.Tests.Validation
{
    public class GroupValidatorTests
    {
        [Fact]
        public void Valid_Passes()
        {
            Assert.True(GroupValidator.Validate(Defaults.NewGroup("生产")).IsValid);
        }

        [Fact]
        public void Name_Empty_Fails()
        {
            var g = Defaults.NewGroup("");
            Assert.Equal(ValidationKeys.Required, GroupValidator.Validate(g).Errors["name"]);
        }

        [Fact]
        public void Name_TooLong_Fails()
        {
            var g = Defaults.NewGroup(new string('g', 256));
            Assert.Equal(ValidationKeys.NameTooLong, GroupValidator.Validate(g).Errors["name"]);
        }

        [Theory]
        [InlineData("#FFF")]
        [InlineData("#GG0000")]
        [InlineData("FF0000")]
        [InlineData("")]
        [InlineData("#1234567")]
        public void Color_Invalid_Fails(string color)
        {
            var g = Defaults.NewGroup("g");
            g.Color = color;
            Assert.Equal(ValidationKeys.ColorFormat, GroupValidator.Validate(g).Errors["color"]);
        }

        [Theory]
        [InlineData("#4F8CFF")]
        [InlineData("#abcdef")]
        [InlineData("#000000")]
        public void Color_Valid_Passes(string color)
        {
            var g = Defaults.NewGroup("g");
            g.Color = color;
            Assert.True(GroupValidator.Validate(g).IsValid);
        }
    }
}
