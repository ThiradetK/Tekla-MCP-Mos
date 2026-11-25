using System;

namespace TeklaMcp.Tekla
{
    /// <summary>Expected Tekla failure that a tool reports as an error result (not a crash).</summary>
    internal sealed class TeklaException : Exception
    {
        /// <summary>not_installed | not_connected | no_model | tekla_timeout | tekla_error</summary>
        public string Code { get; }
        public TeklaException(string code, string message, Exception inner = null) : base(message, inner) { Code = code; }
    }
}
