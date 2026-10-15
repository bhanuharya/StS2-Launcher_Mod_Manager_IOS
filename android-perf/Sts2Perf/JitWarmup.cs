using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using Godot;

namespace STS2Perf;

// jit_warmup=on (experimental): compiles, at the main menu, the game code that
// earlier sessions needed, so it isn't compiled mid-fight.
//
// On Android the game's .NET code runs on Mono's JIT, which compiles every
// method the first time it runs: the first shiv volley or the first new card
// of a session can stall a frame while hundreds of methods compile (the frame
// log's hitch lines show it as "jit +N methods").
//
// While it's on, every game method Mono compiles (JitEvents) is added to
// user://sts2perf_jit.txt. From the next launch on, once the main menu is up,
// those are compiled again ahead of time: RuntimeMethodHandle.
// GetFunctionPointer makes Mono compile a method on the spot
// (RuntimeHelpers.PrepareMethod is a no-op on Mono). It runs on the main
// thread, SliceMs per frame, so the menu keeps drawing. The list grows as you
// play; the first session with it on only records.
//
// Only methods the game itself compiled in normal play are warmed, never the
// whole assembly: compiling a method also runs the static initializers of
// classes it reads, and code the launcher disables (Steam's own startup, say)
// must stay untouched. That includes generic code instantiated over the game's
// types (async state machine plumbing, LINQ, collections of game objects),
// which JitNames turns back into methods from the names Mono reports. Lines
// are "type::method::signature" (0.4.2.5-0.4.2.8 wrote "type::method").
internal static class JitWarmup
{
    private const double SliceMs = 6;
    private const double FlushEveryMs = 10_000;
    private const string FileName = "sts2perf_jit.txt";

    private static readonly HashSet<string> Known = new();
    private static readonly List<string> Unsaved = new();
    private static bool _loaded;
    private static readonly Stopwatch SinceFlush = Stopwatch.StartNew();

    private static bool _started;
    private static List<string> _todo;
    private static int _next;
    private static int _compiled;
    private static int _missing;
    private static int _failed;
    private static readonly List<string> Failures = new();
    private static JitNames.Resolver _resolver;
    private static readonly Stopwatch Busy = new();
    private static readonly Stopwatch Wall = new();
    private static long _jitMethodsBefore;

    private static string FilePath => Path.Combine(OS.GetUserDataDir(), FileName);

    // Main thread, from JitEvents: remember game methods the JIT compiled.
    public static void Record(JitEvents.Jitted jitted)
    {
        if (!PerfSettings.JitWarmup)
            return;
        EnsureLoaded();
        if (!JitNames.ShouldRecord(jitted.Type, jitted.Method, jitted.Signature))
            return;
        var key = $"{jitted.Type}::{jitted.Method}::{jitted.Signature}";
        if (Known.Add(key))
            Unsaved.Add(key);
        if (Unsaved.Count > 0 && SinceFlush.Elapsed.TotalMilliseconds >= FlushEveryMs)
            Flush();
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                foreach (var line in File.ReadAllLines(FilePath))
                    if (line.Contains("::"))
                        Known.Add(line.Trim());
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"jit warm-up: can't read {FileName}: {ex.Message}");
        }
    }

    private static void Flush()
    {
        SinceFlush.Restart();
        try
        {
            File.AppendAllLines(FilePath, Unsaved);
            Unsaved.Clear();
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"jit warm-up: can't write {FileName}: {ex.Message}");
        }
    }

    // From PerfRuntime once the game's UI is up; starts at most once.
    public static void MaybeStart()
    {
        if (_started || !PerfSettings.JitWarmup || Engine.GetMainLoop() is not SceneTree tree)
            return;
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "sts2"))
            return;
        _started = true;
        EnsureLoaded();
        _todo = Known.ToList();
        if (_todo.Count == 0)
        {
            PerfEntry.Log($"jit warm-up: nothing recorded yet; recording this session's methods for the next launch");
            return;
        }
        _resolver = new JitNames.Resolver(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic).ToArray());
        _jitMethodsBefore = JitInfo.GetCompiledMethodCount();
        Wall.Start();
        tree.ProcessFrame += OnFrame;
        PerfEntry.Log($"jit warm-up: compiling {_todo.Count} recorded methods, {SliceMs:0} ms per frame");
    }

    private static void OnFrame()
    {
        if (!PerfSettings.JitWarmup)
        {
            Finish("stopped (turned off)");
            return;
        }
        Busy.Start();
        var slice = Stopwatch.StartNew();
        try
        {
            while (slice.Elapsed.TotalMilliseconds < SliceMs)
            {
                if (_next >= _todo.Count)
                {
                    Busy.Stop();
                    Finish("done");
                    return;
                }
                Warm(_todo[_next++]);
            }
        }
        finally
        {
            Busy.Stop();
        }
    }

    private static void Warm(string entry)
    {
        var parts = entry.Split("::", 3);
        if (parts.Length < 2 || parts[1] == ".cctor")
        {
            _missing++;
            return;
        }
        try
        {
            var type = _resolver.Type(parts[0]);
            var methods = type == null || type.ContainsGenericParameters
                ? new List<MethodBase>()
                : _resolver.Methods(type, parts[1], parts.Length > 2 ? parts[2] : null);
            if (methods.Count == 0)
            {
                _missing++;
                return;
            }
            foreach (var method in methods)
            {
                method.MethodHandle.GetFunctionPointer();
                _compiled++;
            }
        }
        catch (Exception ex)
        {
            _failed++;
            if (ex is TargetInvocationException { InnerException: { } inner })
                ex = inner;
            // A static initializer that threw stays broken: log every one.
            if (Failures.Count < 5 || ex is TypeInitializationException)
                Failures.Add($"{entry}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Finish(string how)
    {
        if (Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame -= OnFrame;
        Wall.Stop();
        PerfEntry.Log(
            $"jit warm-up {how}: {_compiled} methods in {Wall.Elapsed.TotalSeconds:0} s"
                + $" ({Busy.Elapsed.TotalSeconds:0.0} s of frame time, {JitInfo.GetCompiledMethodCount() - _jitMethodsBefore} newly compiled),"
                + $" {_missing} recorded methods not found, {_failed} failed"
        );
        foreach (var failure in Failures)
            PerfEntry.Log($"jit warm-up failure: {failure}");
        _todo = new List<string>();
        _resolver = null;
    }
}
