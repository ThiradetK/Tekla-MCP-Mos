using System.IO;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;
using Xunit;

namespace TeklaMcp.Tests
{
    public class RegistrarTests
    {
        private const string Exe = @"C:\Users\x\AppData\Local\Programs\TeklaMcp\TeklaMcp.exe";

        [Fact]
        public void AddEntry_KeepsOtherServersAndKeys()
        {
            string json = "{\"preferences\":{\"x\":1},\"mcpServers\":{\"other\":{\"command\":\"o.exe\"},\"tekla\":{\"command\":\"old.exe\"}}}";
            var r = JObject.Parse(Registrar.AddEntry(json, Exe));
            Assert.Equal(1, (int)r["preferences"]["x"]);
            Assert.Equal("o.exe", (string)r["mcpServers"]["other"]["command"]);
            Assert.Equal(Exe, (string)r["mcpServers"]["tekla"]["command"]);
            Assert.Empty((JArray)r["mcpServers"]["tekla"]["args"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{}")]
        [InlineData("{\"mcpServers\":null}")]
        public void AddEntry_CreatesMissingParts(string json) =>
            Assert.Equal(Exe, (string)JObject.Parse(Registrar.AddEntry(json, Exe))["mcpServers"]["tekla"]["command"]);

        [Fact]
        public void RemoveEntry_RemovesOnlyTekla()
        {
            string json = Registrar.AddEntry("{\"mcpServers\":{\"other\":{}}}", Exe);
            var r = JObject.Parse(Registrar.RemoveEntry(json, Exe, f => true));
            Assert.Null(r["mcpServers"]["tekla"]);
            Assert.NotNull(r["mcpServers"]["other"]);
            Assert.Null(Registrar.RemoveEntry("{\"mcpServers\":{\"other\":{}}}", Exe));
            Assert.Null(Registrar.RemoveEntry("", Exe));
        }

        [Fact]
        public void RemoveEntry_KeepsEntryOfAnotherExistingCopy()
        {
            string dev = Registrar.AddEntry(null, @"D:\dev\TeklaMcp.exe");
            Assert.Null(Registrar.RemoveEntry(dev, Exe, f => true));                  // other copy still exists -> keep
            Assert.NotNull(Registrar.RemoveEntry(dev, Exe, f => false));              // stale entry -> remove
        }

        [Fact]
        public void DesktopConfigPaths_PrefersStoreFolders()
        {
            string root = Path.Combine(Path.GetTempPath(), "teklamcp-reg-" + System.Guid.NewGuid().ToString("N"));
            string local = Path.Combine(root, "Local"), roaming = Path.Combine(root, "Roaming");
            try
            {
                Directory.CreateDirectory(local);
                Assert.Equal(new[] { Path.Combine(roaming, "Claude", "claude_desktop_config.json") }, Registrar.DesktopConfigPaths(local, roaming));
                Directory.CreateDirectory(Path.Combine(local, "Claude-Data", "claude_abc123"));
                Directory.CreateDirectory(Path.Combine(local, "Claude-Data", "other"));
                Assert.Equal(new[] { Path.Combine(local, "Claude-Data", "claude_abc123", "claude_desktop_config.json") }, Registrar.DesktopConfigPaths(local, roaming));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
