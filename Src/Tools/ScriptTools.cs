using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Lib.GAB.Tools;
using Mono.CSharp;
using UnityEngine;

namespace ValBridgeServer.Tools
{
    /// <summary>
    /// run_script — execute arbitrary C# INSIDE the live Valheim client via a persistent
    /// Mono.CSharp.Evaluator (the same engine UnityExplorer's C# console wraps, referenced
    /// standalone here — no UnityExplorer dependency).
    ///
    /// This is the "full in-game C# Evaluator" scripting surface: an agent ships a C#
    /// snippet, it compiles + runs on Unity's MAIN THREAD against every loaded game/BepInEx
    /// assembly, and we return the REPL return value, captured stdout, and any compiler
    /// diagnostics. Multi-step in-world checks become ONE atomic op instead of N racy MCP
    /// round-trips, and every live field/method/type is reachable.
    ///
    /// Construction (CompilerSettings, appdomain assembly import, the Compile→CompiledMethod
    /// →invoke loop, the report-printer error count) is ported from UnityExplorer's
    /// ScriptEvaluator/ConsoleController — the proven-in-this-game recipe — NOT invented.
    ///
    /// 🔴 MAIN-THREAD LAW: Unity object access from the GABP worker thread hard-crashes the
    /// client (Graphics device null → fatal signal). Every evaluation is marshalled onto the
    /// MainThreadDispatcher and awaited via TaskCompletionSource, exactly like the other tools.
    /// </summary>
    public class ScriptTools
    {
        // Persistent across calls => REPL variables and compiled classes survive, like a session.
        private static Evaluator? _evaluator;
        private static CapturingReportPrinter? _printer;
        private static readonly object _lock = new object();

        // Assemblies we never reference (BCL noise / self) — mirrors UE's StdLib skip set.
        private static readonly HashSet<string> StdLibSkip = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "mscorlib", "System.Core", "System", "System.Xml", "completions"
        };

        [Tool("run_script", Description =
            "Execute arbitrary C# inside the live Valheim client and return the value of the last expression, " +
            "captured stdout (Console.WriteLine / print()), and any compiler errors. Runs on Unity's main thread " +
            "against all loaded game assemblies, so any live object/field/method/type is reachable (e.g. " +
            "Player.m_localPlayer, ZNet.instance, Minimap.instance). REPL variables and classes you define PERSIST " +
            "across calls until reset:true. End with a bare expression to get its value back (e.g. " +
            "'Player.m_localPlayer.transform.position'). Use Log(obj) or Console.WriteLine to emit text.")]
        public object RunScript(
            [ToolParameter(Description = "C# source to compile and execute. A trailing bare expression is returned as 'result'. Statements, variable defs, and class defs are all allowed (classes persist).")] string code,
            [ToolParameter(Description = "If true, discard all persistent REPL state (variables, defined classes, extra references) and start a fresh evaluator before running this code. Default false.")] bool reset = false,
            [ToolParameter(Description = "Max seconds to wait for the script to finish on the main thread before returning a timeout error. Default 30.")] double timeoutSeconds = 30.0)
        {
            if (string.IsNullOrWhiteSpace(code))
                return new { success = false, error = "code is empty" };

            var tcs = new TaskCompletionSource<object>();

            MainThreadDispatcher.Instance.Enqueue(() =>
            {
                try
                {
                    tcs.SetResult(EvaluateOnMainThread(code, reset));
                }
                catch (Exception ex)
                {
                    // Never let an exception escape the dispatcher (would be swallowed and hang the TCS).
                    tcs.SetResult(new
                    {
                        success = false,
                        error = "host exception: " + ex.Message,
                        exceptionType = ex.GetType().FullName,
                        stackTrace = ex.StackTrace
                    });
                }
            });

            // Bounded wait so a runaway/never-scheduled script can't wedge the GABP handler forever.
            if (!tcs.Task.Wait(TimeSpan.FromSeconds(Math.Max(1.0, timeoutSeconds))))
            {
                return new
                {
                    success = false,
                    error = $"script did not complete within {timeoutSeconds}s (still running on main thread, or main thread blocked)"
                };
            }
            return tcs.Task.Result;
        }

        // ── everything below runs ON THE MAIN THREAD ────────────────────────────────────────

        private static object EvaluateOnMainThread(string code, bool reset)
        {
            lock (_lock)
            {
                if (reset)
                {
                    try { _evaluator?.Interrupt(); } catch { /* best effort */ }
                    _evaluator = null;
                    _printer = null;
                }

                if (_evaluator == null)
                    EnsureEvaluator();

                var printer = _printer!;
                var evaluator = _evaluator!;

                printer.Reset();

                // Capture Console.Out for the duration of the invoke so print()/Console.WriteLine surface.
                var stdout = new StringWriter();
                var prevOut = System.Console.Out;
                object? replResult = null;
                bool resultSet = false;
                bool ran = false;
                string? runtimeError = null;
                string? runtimeStack = null;

                try
                {
                    System.Console.SetOut(stdout);

                    // UnityExplorer's proven flow: Compile() returns a CompiledMethod for a REPL
                    // expression; returns null for pure statements / class defs (or on error, which
                    // the printer records). Compile() also swallows a trailing incomplete fragment,
                    // so a compile error surfaces via printer.ErrorsCount, not an exception.
                    CompiledMethod? compiled = null;
                    try
                    {
                        compiled = evaluator.Compile(code);
                    }
                    catch (Exception ce)
                    {
                        // Some malformed input throws inside the compiler rather than reporting.
                        runtimeError = "compile exception: " + ce.Message;
                    }

                    if (printer.ErrorsCount > 0)
                    {
                        // Compilation failed — return structured diagnostics, do NOT invoke.
                        return new
                        {
                            success = false,
                            phase = "compile",
                            errors = printer.Errors,
                            warnings = printer.Warnings,
                            output = NullIfEmpty(stdout.ToString())
                        };
                    }

                    if (compiled != null)
                    {
                        // REPL expression/statement with a return slot — invoke it.
                        // Compilation already succeeded here, so any throw below is a RUNTIME fault.
                        ran = true;
                        try
                        {
                            compiled(ref replResult);
                            resultSet = replResult != null;
                        }
                        catch (Exception re)
                        {
                            runtimeError = re.Message;
                            runtimeStack = re.StackTrace;
                        }
                    }
                    else if (runtimeError == null)
                    {
                        // Pure statements / class definitions compiled with no errors and no return value.
                        ran = true;
                    }
                }
                finally
                {
                    System.Console.SetOut(prevOut);
                }

                if (runtimeError != null)
                {
                    return new
                    {
                        success = false,
                        phase = ran ? "runtime" : "compile",
                        error = runtimeError,
                        stackTrace = runtimeStack,
                        warnings = printer.Warnings,
                        output = NullIfEmpty(stdout.ToString())
                    };
                }

                return new
                {
                    success = true,
                    result = resultSet ? Describe(replResult) : null,
                    resultType = resultSet ? replResult!.GetType().FullName : null,
                    hasReturnValue = resultSet,
                    output = NullIfEmpty(stdout.ToString()),
                    warnings = printer.Warnings
                };
            }
        }

        private static void EnsureEvaluator()
        {
            _printer = new CapturingReportPrinter(TextWriter.Null);

            // Ported from UnityExplorer.CSConsole.ScriptEvaluator.BuildContext — the exact
            // settings proven to compile against Valheim's Mono runtime.
            var settings = new CompilerSettings
            {
                Version = LanguageVersion.Experimental,
                GenerateDebugInfo = false,
                StdLib = true,
                Target = Target.Library,
                WarningLevel = 0,
                EnhancedWarnings = false,
                Unsafe = true
            };
            var ctx = new CompilerContext(settings, _printer);
            var evaluator = new Evaluator(ctx)
            {
                // Gives scripts a base class with Log()/print() and access to the run context.
                InteractiveBaseClass = typeof(ScriptInteraction)
            };

            // Reference every loaded assembly (game, BepInEx, Unity, mods) so scripts can touch
            // anything live — UE's ImportAppdomainAssemblies, minus the BCL noise.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (string.IsNullOrEmpty(name) || StdLibSkip.Contains(name)) continue;
                try { evaluator.ReferenceAssembly(asm); } catch { /* dynamic/reflection-only asms */ }
            }

            // A broad default using-set so common Valheim/Unity work needs no boilerplate.
            // (Errors here are non-fatal; missing namespaces just aren't imported.)
            foreach (var ns in new[]
            {
                "System", "System.Linq", "System.Collections", "System.Collections.Generic",
                "System.Reflection", "UnityEngine", "ValBridgeServer", "ValBridgeServer.Tools"
            })
            {
                try { evaluator.Run("using " + ns + ";"); } catch { }
            }
            _printer.Reset(); // don't let using-import noise leak into the first real call

            _evaluator = evaluator;
        }

        // Render a return value into something JSON-friendly and readable.
        private static object Describe(object? value)
        {
            if (value == null) return "null";
            switch (value)
            {
                case string s: return s;
                case bool or sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return value;
                case Vector3 v: return new { x = v.x, y = v.y, z = v.z };
                case Vector2 v2: return new { x = v2.x, y = v2.y };
                case Quaternion q: return new { x = q.x, y = q.y, z = q.z, w = q.w };
            }
            // Fall back to ToString() for arbitrary objects (agent can drill in with more scripts).
            try { return value.ToString(); }
            catch (Exception e) { return "<ToString threw: " + e.Message + ">"; }
        }

        private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>
        /// Captures compiler diagnostics as structured data while preserving the base class's
        /// ErrorsCount/WarningsCount (Evaluator relies on those to decide compile success).
        /// </summary>
        private sealed class CapturingReportPrinter : StreamReportPrinter
        {
            private readonly List<object> _errors = new List<object>();
            private readonly List<object> _warnings = new List<object>();

            public CapturingReportPrinter(TextWriter tw) : base(tw) { }

            public List<object> Errors => _errors;
            public List<object> Warnings => _warnings;

            public override void Print(AbstractMessage msg, bool showFullPath)
            {
                var entry = new
                {
                    code = msg.Code,
                    text = msg.Text,
                    row = msg.Location.Row,
                    column = msg.Location.Column
                };
                if (msg.IsWarning) _warnings.Add(entry);
                else _errors.Add(entry);
                base.Print(msg, showFullPath); // keeps ErrorsCount/WarningsCount correct
            }

            public void ClearCaptured()
            {
                _errors.Clear();
                _warnings.Clear();
            }

            // Reset both the captured lists AND the base counters between evaluations.
            public new void Reset()
            {
                ClearCaptured();
                base.Reset();
            }
        }
    }

    /// <summary>
    /// Interactive base class for run_script snippets — the type set as the evaluator's
    /// InteractiveBaseClass. Its public static members are callable unqualified from a script
    /// (mirrors UnityExplorer's ScriptInteraction, trimmed to the harness's needs).
    /// </summary>
    public class ScriptInteraction : InteractiveBase
    {
        /// <summary>Print a line to the script's captured stdout (returned as 'output').</summary>
        public static void Log(object message) => System.Console.WriteLine(message?.ToString() ?? "null");

        /// <summary>The live local player, or null at the main menu.</summary>
        public static Player? LocalPlayer => Player.m_localPlayer;
    }
}
