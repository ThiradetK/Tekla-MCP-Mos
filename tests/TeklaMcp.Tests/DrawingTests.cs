using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using TeklaMcp.Tools.Drawing;
using Xunit;

namespace TeklaMcp.Tests
{
    public class DrawingTests
    {
        [Fact]
        public void DrawingId_IsTypeMarkAndCreationTime() =>
            Assert.Equal("cast_unit|[PF.13 - 1]|20261124224847", TeklaDrawing.Id("cast_unit", "[PF.13 - 1]", new DateTime(2026, 11, 24, 22, 48, 47)));

        [Fact]
        public void Tools_HaveExpectedLevels()
        {
            var tools = DrawingTools.Create(null).ToList();
            Assert.Equal(11, tools.Count);
            string[] read = { "tekla_list_drawings", "tekla_get_drawing", "tekla_open_drawing" };
            Assert.All(tools, t =>
            {
                Assert.StartsWith("tekla_", t.Name);
                var expected = read.Contains(t.Name) ? ToolLevel.Read : t.Name == "tekla_delete_drawings" ? ToolLevel.Destructive : ToolLevel.Write;
                Assert.Equal(expected, t.Level);
                if (t.Level != ToolLevel.Read) Assert.Contains("mode", ((JArray)t.InputSchema["required"]).Values<string>());
            });
            var reg = new ToolRegistry();
            foreach (var t in tools) reg.Add(t);
        }
    }
}
