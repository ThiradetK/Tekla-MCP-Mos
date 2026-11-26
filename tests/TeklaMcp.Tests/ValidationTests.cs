using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using TeklaMcp.Tools.Validation;
using Xunit;

namespace TeklaMcp.Tests
{
    public class ValidationTests
    {
        [Theory]
        [InlineData("B16(?)", true)]
        [InlineData("A0(?)", true)]
        [InlineData("", true)]
        [InlineData(null, true)]
        [InlineData("B16", false)]
        [InlineData("C-12", false)]
        public void Unnumbered(string mark, bool expected) => Assert.Equal(expected, TeklaValidate.Unnumbered(mark));

        [Fact]
        public void Tools_AreReadOnlyWithPaging()
        {
            var tools = ValidationTools.Create(null, new Settings { TeklaTimeoutSeconds = 60 }).ToList();
            Assert.Equal(new[] { "tekla_validate_model", "tekla_clash_check" }, tools.Select(t => t.Name));
            Assert.All(tools, t =>
            {
                Assert.Equal(ToolLevel.Read, t.Level);
                Assert.NotNull(t.InputSchema["properties"]["limit"]);
                Assert.NotNull(t.InputSchema["properties"]["offset"]);
            });
            Assert.Contains("55 s", tools[1].Description);   // wait = timeout - 5
            var reg = new ToolRegistry();
            foreach (var t in tools) reg.Add(t);
        }
    }
}
