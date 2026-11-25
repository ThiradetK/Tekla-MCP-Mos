using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tekla;
using TeklaMcp.Tools;
using Xunit;

namespace TeklaMcp.Tests
{
    public class DataTests
    {
        [Theory]
        [InlineData("WF400X200X8X13", "WF400*", true)]
        [InlineData("wf400x200x8x13", "WF400*", true)]
        [InlineData("WF400X200X8X13", "WF400", false)]
        [InlineData("SS400", "SS4?0", true)]
        [InlineData("C40", "C4*", true)]
        [InlineData("H(1)", "H(1)", true)]
        [InlineData(null, "*", true)]
        [InlineData("anything", null, true)]
        public void Wildcard_Matches(string value, string pattern, bool expected) => Assert.Equal(expected, Wildcard.IsMatch(value, pattern));

        [Fact]
        public void Quantity_GroupsAndSums()
        {
            var rows = new[]
            {
                JObject.Parse("{\"profile\":\"H200\",\"material\":\"SS400\",\"weight_kg\":10.5,\"length_mm\":1000,\"area_mm2\":5,\"volume_mm3\":7}"),
                JObject.Parse("{\"profile\":\"H200\",\"material\":\"SS400\",\"weight_kg\":4.5,\"length_mm\":500}"),
                JObject.Parse("{\"profile\":\"12\",\"material\":\"SD40\",\"weight_kg\":2,\"length_mm\":3000,\"number_of_bars\":4}"),
            };
            var s = Quantity.Summarize(rows, new[] { "profile", "material" });
            var g = (JArray)s["groups"];
            Assert.Equal(2, g.Count);
            var h = g.First(x => (string)x["profile"] == "H200");
            Assert.Equal(2, (int)h["object_count"]);
            Assert.Equal(15.0, (double)h["total_weight_kg"]);
            Assert.Equal(1500.0, (double)h["total_length_mm"]);
            Assert.Equal(4, (int)s["total"]["bar_count"]);
            Assert.Equal(17.0, (double)s["total"]["total_weight_kg"]);
            Assert.Equal(3, (int)s["total"]["object_count"]);
        }

        [Fact]
        public void Export_ResolvePath_RejectsUnsafeAndExisting()
        {
            string dir = Path.Combine(Path.GetTempPath(), "teklamcp-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Assert.Equal(Path.Combine(dir, "boq.csv"), TableExport.ResolvePath(dir, "boq", "csv"));
            Assert.Throws<ArgumentException>(() => TableExport.ResolvePath(dir, @"..\evil", "csv"));
            Assert.Throws<ArgumentException>(() => TableExport.ResolvePath(dir, @"C:\x\a.csv", "csv"));
            Assert.Throws<ArgumentException>(() => TableExport.ResolvePath(dir, "sub/a", "csv"));
            File.WriteAllText(Path.Combine(dir, "exists.json"), "{}");
            Assert.Throws<ArgumentException>(() => TableExport.ResolvePath(dir, "exists.json", "json"));
        }

        [Fact]
        public void Export_WritesCsvJsonXlsx()
        {
            string dir = Path.Combine(Path.GetTempPath(), "teklamcp-tests", Guid.NewGuid().ToString("N"));
            var cols = new[] { "name", "weight_kg" };
            var rows = new[] { JObject.Parse("{\"name\":\"คาน, B1\",\"weight_kg\":12.5}"), JObject.Parse("{\"name\":\"say \\\"hi\\\"\"}") };
            foreach (var f in TableExport.Formats)
            {
                string p = TableExport.ResolvePath(dir, "t", f);
                TableExport.Write(p, f, cols, rows, new JObject());
                Assert.True(new FileInfo(p).Length > 0);
            }
            var csv = File.ReadAllLines(Path.Combine(dir, "t.csv"));
            Assert.Equal("name,weight_kg", csv[0].TrimStart('\uFEFF'));
            Assert.Equal("\"คาน, B1\",12.5", csv[1]);
            Assert.Equal("\"say \"\"hi\"\"\",", csv[2]);
            Assert.Equal(2, ((JArray)JObject.Parse(File.ReadAllText(Path.Combine(dir, "t.json")))["rows"]).Count);
        }

        [Fact]
        public void PlaneAxis_FromGridPlaneAxes()
        {
            // Probe data: label 1, origin (0,0,0), AxisX (0,54000,0), AxisY (0,0,9000) -> plane normal along X.
            Assert.Equal(("x", (double?)0), TeklaText.PlaneAxis(0, 0, 0, 0, 54000, 0, 0, 0, 9000));
            Assert.Equal(("y", (double?)6000), TeklaText.PlaneAxis(0, 6000, 0, 36000, 0, 0, 0, 0, 9000));
            Assert.Equal(("z", (double?)3500), TeklaText.PlaneAxis(0, 0, 3500, 1, 0, 0, 0, 1, 0));
            Assert.Equal(("other", (double?)null), TeklaText.PlaneAxis(0, 0, 0, 1, 1, 0, 0, 0, 1));
        }

        [Fact]
        public void RebarTotalLength_UsesBarCount()
        {
            // Probe data: group of 62 bars, LENGTH 1400, WEIGHT 2.212 (one bar), WEIGHT_TOTAL 137.144.
            Assert.Equal(86800, TeklaText.RebarTotalLength(1400, 1400, 1400, 2.212, 137.144, 62));
            Assert.Equal(5360, TeklaText.RebarTotalLength(5360, 5360, 5360, 13.228, 13.2458, 1));
            // Varying bar lengths: total weight / (one bar's kg per mm).
            Assert.Equal(10000, TeklaText.RebarTotalLength(1000, 1500, 500, 2.0, 20.0, 10), 6);
        }

        [Fact]
        public void Filter_ToWorker_ValidatesBbox()
        {
            var ok = ObjectFilter.ToWorker(JObject.Parse("{\"types\":[\"beam\"],\"profile\":\"H*\",\"bbox\":{\"min\":[0,0,0],\"max\":[1,1,1]}}"));
            Assert.Equal("H*", (string)ok["filter"]["profile"]);
            Assert.Equal("beam", (string)ok["types"][0]);
            Assert.Throws<TeklaException>(() => ObjectFilter.ToWorker(JObject.Parse("{\"bbox\":{\"min\":[0,0],\"max\":[1,1,1]}}")));
        }
    }
}
