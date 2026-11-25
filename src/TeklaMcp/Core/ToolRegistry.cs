using System;
using System.Collections.Generic;
using System.Linq;

namespace TeklaMcp.Core
{
    internal sealed class ToolRegistry
    {
        private readonly Dictionary<string, ITool> _tools = new Dictionary<string, ITool>(StringComparer.Ordinal);

        public void Add(ITool tool)
        {
            if (tool == null) throw new ArgumentNullException(nameof(tool));
            if (!tool.Name.StartsWith("tekla_", StringComparison.Ordinal)) throw new ArgumentException("Tool name must start with tekla_: " + tool.Name);
            if (!(tool is IReadTool) && !(tool is IWriteTool)) throw new ArgumentException("Tool must implement IReadTool or IWriteTool: " + tool.Name);
            if (tool.Level == ToolLevel.Read && !(tool is IReadTool)) throw new ArgumentException("READ tool must implement IReadTool: " + tool.Name);
            if (tool.Level != ToolLevel.Read && !(tool is IWriteTool)) throw new ArgumentException("WRITE/DESTRUCTIVE tool must implement IWriteTool: " + tool.Name);
            if (_tools.ContainsKey(tool.Name)) throw new ArgumentException("Duplicate tool: " + tool.Name);
            _tools.Add(tool.Name, tool);
        }

        public bool TryGet(string name, out ITool tool) => _tools.TryGetValue(name ?? "", out tool);

        public IReadOnlyList<ITool> All => _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }
}
