using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using TeklaMcp.Tools.Modify;
using Xunit;

namespace TeklaMcp.Tests
{
    public class WriteTests
    {
        private static readonly string[] Library = { "WF400X200X8X13", "H600X300X170", "SHS100X100X6" };
        private static readonly string[] Prefixes = { "", "PL", "D", "RHS", "SHS", "P", "HI" };

        [Theory]
        [InlineData("WF400X200X8X13", "WF400X200X8X13")]
        [InlineData("wf400x200x8x13", "WF400X200X8X13")]   // library spelling is returned
        [InlineData("500*250", "500*250")]                 // plain numeric (empty prefix)
        [InlineData("200", "200")]
        [InlineData("PL20*200", "PL20*200")]
        [InlineData("D100", "D100")]
        [InlineData("RHS200*100*6", "RHS200*100*6")]
        [InlineData("NOTAPROFILE", null)]
        [InlineData("HEA300", null)]                       // not in SE_Asia
        [InlineData("PL", null)]                           // prefix without parameters
        [InlineData("", null)]
        public void NormalizeProfile(string input, string expected) =>
            Assert.Equal(expected, TeklaText.NormalizeProfile(input, Library, Prefixes));

        [Fact]
        public void WriteTools_AreTwoStepAndNamed()
        {
            var tools = WriteTools.Create(null).ToList();
            Assert.Equal(15, tools.Count);
            Assert.All(tools, t =>
            {
                Assert.StartsWith("tekla_", t.Name);
                Assert.NotEqual(ToolLevel.Read, t.Level);
                Assert.Contains("mode", ((JArray)t.InputSchema["required"]).Values<string>());
                Assert.NotNull(t.InputSchema["properties"]["confirm_token"]);
                Assert.Contains("explicitly approves", t.Description);
            });
            Assert.Equal(ToolLevel.Destructive, tools.Single(t => t.Name == "tekla_delete_objects").Level);
            var reg = new ToolRegistry();
            foreach (var t in tools) reg.Add(t);   // registry rules: IWriteTool for WRITE/DESTRUCTIVE, unique names
        }
    }
}
