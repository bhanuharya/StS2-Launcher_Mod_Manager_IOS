using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// text_fx=native (Perf settings → Text colors) also covers the game's wavy
// text ([sine], RichTextSine): the same wave, without its garbage.
//
// RichTextSine._ProcessCustomFX runs for every character of [sine] text on
// screen on every frame (~42 calls a frame through a 0.4.2.17 log, fights
// and map alike), and each call reads the tag's parameters through
// CharFXTransform.Env. In Godot's C# that getter wraps the native Dictionary
// in a new Godot.Collections.Dictionary, which registers itself with Godot's
// DisposablesTracker (a WeakReference and a ConcurrentDictionary entry) and
// is released by a finalizer: ~2,500 objects with finalizers a second, all
// for the same values, as a tag's parameters don't change. Objects with
// finalizers are the costly kind for Mono's collector (GcTuning).
//
// This runs the game's own arithmetic in place of the method, with its
// constants read from the class, and reads a tag's parameters once: Godot
// keeps one CharFXTransform per tag, so that's what they're cached on. If
// the class or its constants aren't as expected (a game update), it's left
// as the game does it.
internal static class SineText
{
    private const string HarmonyId = "com.sts2perf.sinetext";

    private sealed class TagParams
    {
        public bool HasColor;
        public Color Color;
        public bool Visible;
    }

    private static readonly ConditionalWeakTable<CharFXTransform, TagParams> Cache = new();
    private static readonly Variant ColorKey = Variant.From("color");
    private static readonly Variant VisibleKey = Variant.From("visible");

    private static Harmony _harmony;
    private static bool _on;
    private static bool _unavailable;
    private static Func<object, bool> _shouldTransform;
    private static float _amplitude;
    private static float _frequency;
    private static float _speed;

    // From PerfRuntime.ApplyAll once the game's UI is up.
    public static void Apply()
    {
        var on = PerfSettings.Value("text_fx") == "native";
        if (on == _on || _unavailable)
            return;
        if (on)
        {
            var method = Prepare();
            if (method == null)
            {
                _unavailable = true;
                PerfEntry.Log("wavy text: the game's [sine] effect isn't as expected in this game version, left as the game does it");
                return;
            }
            _harmony ??= new Harmony(HarmonyId);
            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(SineText), nameof(Process)));
            PerfEntry.Log("wavy text: the game's wave, its tag's settings read once instead of per letter per frame");
        }
        else
        {
            _harmony.UnpatchAll(HarmonyId);
            PerfEntry.Log("wavy text: as the game");
        }
        _on = on;
    }

    private static MethodInfo Prepare()
    {
        var type = AccessTools.TypeByName("MegaCrit.Sts2.Core.RichTextTags.RichTextSine");
        var method = type == null ? null : AccessTools.Method(type, "_ProcessCustomFX", new[] { typeof(CharFXTransform) });
        var should = type == null ? null : AccessTools.Method(type, "ShouldTransformText", Type.EmptyTypes);
        float? Const(string name) => AccessTools.Field(type, name) is { IsLiteral: true } f && f.GetRawConstantValue() is float v ? v : null;
        if (method == null || should == null || should.ReturnType != typeof(bool)
            || Const("_amplitude") is not { } amplitude || Const("_frequency") is not { } frequency || Const("_speed") is not { } speed)
            return null;
        _amplitude = amplitude;
        _frequency = frequency;
        _speed = speed;
        // The effect's own check for the game's "text effects" setting
        // (protected, on its base class).
        var call = new DynamicMethod("ShouldTransformText", typeof(bool), new[] { typeof(object) }, typeof(SineText).Module, true);
        var il = call.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, should.DeclaringType);
        il.Emit(should.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, should);
        il.Emit(OpCodes.Ret);
        _shouldTransform = (Func<object, bool>)call.CreateDelegate(typeof(Func<object, bool>));
        return method;
    }

    // RichTextSine._ProcessCustomFX, as the game writes it, but with the
    // tag's color and visibility from the cache.
    private static bool Process(object __instance, CharFXTransform __0, ref bool __result)
    {
        var charFx = __0;
        try
        {
            if (!_shouldTransform(__instance))
            {
                __result = false;
                return false;
            }
            if (!Cache.TryGetValue(charFx, out var tag))
            {
                tag = Read(charFx);
                Cache.AddOrUpdate(charFx, tag);
            }
            var num = (float)(charFx.ElapsedTime * _speed + (double)((float)charFx.RelativeIndex * 0.1f));
            var y = _amplitude * Mathf.Sin(num * (float)Math.PI * 2f * _frequency);
            charFx.Offset += new Vector2(0f, y);
            if (tag.HasColor)
                charFx.Color = tag.Color;
            charFx.Visible = tag.Visible;
            __result = true;
            return false;
        }
        catch
        {
            return true; // the game's own method
        }
    }

    private static TagParams Read(CharFXTransform charFx)
    {
        using var env = charFx.Env;
        var tag = new TagParams { Visible = !env.ContainsKey(VisibleKey) || (bool)env[VisibleKey] };
        if (env.TryGetValue(ColorKey, out var color))
        {
            tag.HasColor = true;
            tag.Color = (Color)color;
        }
        return tag;
    }
}
