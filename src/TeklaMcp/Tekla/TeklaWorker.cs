using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TeklaMcp.Core;

namespace TeklaMcp.Tekla
{
    /// <summary>
    /// Server-side handle to the "--worker" child process that runs all Tekla API calls (see WorkerHost).
    /// When the worker reports reset=true (Tekla not reachable or connection stale), the worker is replaced
    /// by a fresh process and the call is retried once. A call slower than the timeout returns tekla_timeout;
    /// the worker keeps running it (Tekla calls cannot be aborted) and later calls queue behind it.
    /// </summary>
    internal sealed class TeklaWorker : IDisposable
    {
        private sealed class Child
        {
            public Process Process;
            public StreamWriter Input;
            public readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> Pending = new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        }

        private readonly object _sync = new object();
        private readonly string _exe;
        private readonly TimeSpan _timeout;
        private Child _child;
        private long _nextId;

        public TeklaWorker(string exePath, TimeSpan timeout) { _exe = exePath; _timeout = timeout; }

        /// <summary>Runs a worker operation. Throws TeklaException on failure.</summary>
        /// <param name="retryOnReset">false for operations that must not run twice (write tools).</param>
        /// <param name="timeout">Overrides the settings timeout for slow operations (drawing export through Tekla dialogs).</param>
        public JObject Call(string op, JObject args = null, bool retryOnReset = true, TimeSpan? timeout = null)
        {
            JObject r = Send(op, args, timeout ?? _timeout);
            if ((bool?)r["reset"] == true)
            {
                Log.Info("Tekla not reachable from current worker (" + (r["code"] ?? "not connected") + "); starting a fresh worker.");
                Restart();
                if (retryOnReset) r = Send(op, args, timeout ?? _timeout);
            }
            if ((bool?)r["ok"] != true) throw new TeklaException((string)r["code"] ?? "tekla_error", (string)r["message"] ?? "Tekla worker error");
            return (JObject)r["result"];
        }

        private JObject Send(string op, JObject args, TimeSpan timeout)
        {
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            string line = new JObject { ["id"] = id, ["op"] = op, ["args"] = args ?? new JObject() }.ToString(Formatting.None);
            lock (_sync)
            {
                if (_child == null || _child.Process.HasExited) _child = Start();
                _child.Pending[id] = tcs;
                try { _child.Input.WriteLine(line); _child.Input.Flush(); }
                catch (IOException)
                {
                    // Worker died between the HasExited check and the write: start a new one and resend once.
                    _child.Pending.TryRemove(id, out _);
                    _child = Start();
                    _child.Pending[id] = tcs;
                    _child.Input.WriteLine(line); _child.Input.Flush();
                }
            }
            if (!((IAsyncResult)tcs.Task).AsyncWaitHandle.WaitOne(timeout))
            {
                lock (_sync) _child?.Pending.TryRemove(id, out _);
                throw new TeklaException("tekla_timeout", "Tekla did not respond within " + (int)timeout.TotalSeconds + " s. Tekla may be busy or showing a dialog.");
            }
            return tcs.Task.Result;
        }

        private void Restart()
        {
            lock (_sync)
            {
                Stop(_child, "connection_lost", "Tekla worker was restarted.");
                _child = Start();
            }
        }

        private Child Start()
        {
            var psi = new ProcessStartInfo(_exe, "--worker")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            var child = new Child { Process = Process.Start(psi) };
            child.Input = new StreamWriter(child.Process.StandardInput.BaseStream, new UTF8Encoding(false)) { NewLine = "\n" };
            // Worker logs to its own file; stderr is drained only so its pipe never fills up.
            child.Process.ErrorDataReceived += (s, e) => { };
            child.Process.BeginErrorReadLine();
            new Thread(() => ReadLoop(child)) { IsBackground = true, Name = "TeklaWorkerReader" }.Start();
            Log.Info("Tekla worker started, pid " + child.Process.Id);
            return child;
        }

        private static void ReadLoop(Child child)
        {
            try
            {
                string line;
                while ((line = child.Process.StandardOutput.ReadLine()) != null)
                {
                    JObject m;
                    try { m = JObject.Parse(line); } catch (JsonException) { Log.Warn("worker stdout (not JSON): " + line); continue; }
                    long id = (long?)m["id"] ?? -1;
                    if (child.Pending.TryRemove(id, out var tcs)) tcs.TrySetResult(m);
                }
            }
            catch (Exception ex) { Log.Warn("worker read failed: " + ex.Message); }
            FailAll(child, "worker_exited", "Tekla worker process exited.");
        }

        private static void FailAll(Child child, string code, string message)
        {
            foreach (var id in child.Pending.Keys)
                if (child.Pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(new JObject { ["id"] = id, ["ok"] = false, ["code"] = code, ["message"] = message });
        }

        private static void Stop(Child child, string code, string message)
        {
            if (child == null) return;
            FailAll(child, code, message);
            try { child.Input.Close(); } catch { /* already gone */ }
            try { if (!child.Process.WaitForExit(2000)) child.Process.Kill(); } catch { /* already gone */ }
        }

        public void Dispose()
        {
            lock (_sync) { Stop(_child, "worker_exited", "Server is shutting down."); _child = null; }
        }
    }
}

