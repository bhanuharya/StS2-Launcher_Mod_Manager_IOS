using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using Godot;

namespace STS2Perf;

// Which methods Mono's JIT compiles, as it compiles them. The runtime reports
// each one (MethodJittingStarted) through its in-process event source; the APK
// ships Mono's diagnostics_tracing component, which carries it. Events arrive
// on a runtime thread a little after the fact, so the main thread drains them
// once per frame into Recent (the last few seconds, for FrameLog's hitch
// lines) and hands them to JitWarmup's recorder.
//
// Names look like "MegaCrit.Sts2.Core.Foo.<PlayAsync>d__12" / "MoveNext":
// nested types joined with '.', generic instances ending in '>'.
internal sealed class JitEvents : EventListener
{
    public readonly record struct Jitted(DateTime TimeUtc, string Type, string Method, string Signature);

    private const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";
    private const long JitKeyword = 0x10;
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(10);

    private static JitEvents _listener;
    private static bool _hooked;
    private static long _received;

    public static readonly List<Jitted> Recent = new();
    private readonly ConcurrentQueue<Jitted> _queue = new();

    public static bool Running => _listener != null;
    public static long Received => _received;

    // Running while the frame log or JIT warm-up wants it.
    public static void Configure()
    {
        var wanted = PerfSettings.FrameLogSeconds > 0 || PerfSettings.JitWarmup;
        try
        {
            if (wanted && _listener == null)
            {
                _listener = new JitEvents();
                if (!_hooked && Engine.GetMainLoop() is SceneTree tree)
                {
                    tree.ProcessFrame += Drain;
                    _hooked = true;
                }
                PerfEntry.Log("jit events: listening");
            }
            else if (!wanted && _listener != null)
            {
                _listener.Dispose();
                _listener = null;
                Recent.Clear();
                PerfEntry.Log("jit events: stopped");
            }
        }
        catch (Exception ex)
        {
            _listener = null;
            PerfEntry.Log($"jit events unavailable: {ex.Message}");
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == RuntimeSource)
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)JitKeyword);
    }

    // Runtime thread: only queue.
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName == null || !e.EventName.StartsWith("MethodJittingStarted", StringComparison.Ordinal))
            return;
        var names = e.PayloadNames;
        var ns = names?.IndexOf("MethodNamespace") ?? -1;
        var name = names?.IndexOf("MethodName") ?? -1;
        var signature = names?.IndexOf("MethodSignature") ?? -1;
        if (ns < 0 || name < 0)
            return;
        _queue.Enqueue(new Jitted(
            e.TimeStamp.ToUniversalTime(),
            e.Payload[ns] as string ?? "",
            e.Payload[name] as string ?? "",
            signature >= 0 ? e.Payload[signature] as string ?? "" : ""
        ));
    }

    private static void Drain()
    {
        var listener = _listener;
        if (listener == null)
            return;
        try
        {
            while (listener._queue.TryDequeue(out var jitted))
            {
                _received++;
                Recent.Add(jitted);
                JitWarmup.Record(jitted);
            }
            var cutoff = DateTime.UtcNow - Keep;
            var old = 0;
            while (old < Recent.Count && Recent[old].TimeUtc < cutoff)
                old++;
            if (old > 0)
                Recent.RemoveRange(0, old);
        }
        catch (Exception ex)
        {
            PerfEntry.Log($"jit events: {ex.Message}");
        }
    }

    // "Sts2.Core.Combat.CardPlay x12, ..." for the methods compiled between
    // two times: the types with the most, game namespace prefix dropped.
    public static string TopTypes(DateTime fromUtc, DateTime toUtc, int count)
    {
        var byType = new Dictionary<string, int>();
        foreach (var j in Recent)
        {
            if (j.TimeUtc < fromUtc || j.TimeUtc > toUtc)
                continue;
            var type = j.Type.StartsWith("MegaCrit.", StringComparison.Ordinal) ? j.Type[9..] : j.Type;
            byType[type] = byType.TryGetValue(type, out var n) ? n + 1 : 1;
        }
        var top = new List<KeyValuePair<string, int>>(byType);
        top.Sort((a, b) => b.Value.CompareTo(a.Value));
        var parts = new List<string>();
        for (var i = 0; i < top.Count && i < count; i++)
            parts.Add($"{top[i].Key} x{top[i].Value}");
        return string.Join(", ", parts);
    }
}
