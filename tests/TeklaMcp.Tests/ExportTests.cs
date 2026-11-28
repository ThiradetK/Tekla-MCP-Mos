using System;
using System.IO;
using System.Linq;
using TeklaMcp.Core;
using TeklaMcp.Tools.Export;
using Xunit;

namespace TeklaMcp.Tests
{
    public class ExportTests
    {
        [Fact]
        public void ResolveFolder_NewFolderInsideExportDir()
        {
            string root = Path.Combine(Path.GetTempPath(), "teklamcp-exp-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                Assert.Equal(Path.Combine(root, "ifc_tower"), TableExport.ResolveFolder(root, "ifc_tower"));
                Directory.CreateDirectory(Path.Combine(root, "ifc_tower"));
                Assert.Throws<ArgumentException>(() => TableExport.ResolveFolder(root, "ifc_tower"));      // existing folder: never overwritten
                Assert.Throws<ArgumentException>(() => TableExport.ResolveFolder(root, @"..\escape"));
                Assert.Throws<ArgumentException>(() => TableExport.ResolveFolder(root, @"sub\dir"));
                Assert.Throws<ArgumentException>(() => TableExport.ResolveFolder(root, " "));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("[PF.13 - 1]", "PF13-1")]
        [InlineData("[A.2]", "A2")]
        [InlineData("[1]", "1")]
        public void MarkToken_MatchesTeklaFileNames(string mark, string token) => Assert.Equal(token, TeklaMcp.Tekla.TeklaDrawingExport.MarkToken(mark));

        [Theory]
        [InlineData("PF13 - 1 - standard.dwg", "PF13 - 1 - standard", true)]
        [InlineData("PF10 - 1 - standard.dwg", "PF13 - 1 - standard", false)]
        [InlineData("1 - Standard - Rev .pdf", "1 - Standard - Rev ", true)]   // GA plot name keeps a trailing space
        [InlineData("101 - Rev .pdf", "1 - Standard - Rev ", false)]           // multidrawing [1] is not the GA [1]
        [InlineData("101 - Rev .pdf", "101 - Rev ", true)]
        [InlineData("x.pdf", "", false)]
        public void MatchesPlotName_ExactPlotFileName(string file, string plot, bool expected) =>
            Assert.Equal(expected, TeklaMcp.Tekla.TeklaDrawingExport.MatchesPlotName(file, plot));

        [Theory]
        [InlineData("tower.v2", "tower.v2")]       // was cut to "tower" by GetFileNameWithoutExtension
        [InlineData("rev1.2.ifc", "rev1.2")]
        [InlineData("  tower.IFC ", "tower")]
        [InlineData(null, "")]
        public void TrimExtension_RemovesOnlyIfc(string input, string expected) => Assert.Equal(expected, TableExport.TrimExtension(input, ".ifc"));

        [Fact]
        public void Tools_AreReadLevel()
        {
            var tools = ExportTools.Create(null, new Settings()).ToList();
            Assert.Equal(new[] { "tekla_export_ifc", "tekla_export_nc", "tekla_export_drawings" }, tools.Select(t => t.Name));
            Assert.All(tools, t => Assert.Equal(ToolLevel.Read, t.Level));
            var reg = new ToolRegistry();
            foreach (var t in tools) reg.Add(t);
        }
    }
}

