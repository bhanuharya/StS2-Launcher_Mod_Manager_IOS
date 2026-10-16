using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Godot;
using HarmonyLib;

namespace STS2Perf;

// text_fx=native (Perf settings → Text colors): the game's colored words and
// numbers are drawn with Godot's own [color] instead of the game's C# text
// effects. Same colors, nothing else changes on screen.
//
// The game colors text in its labels (MegaRichTextLabel) with custom effects:
// "[gold]6[/gold]", [blue], [red], [green], [aqua], [orange], [pink], [purple]
// are RichTextEffect classes whose _ProcessCustomFX sets one constant color
// (StsColors). Godot redraws a label holding any custom effect on every
// frame, and calls the effect's C# _ProcessCustomFX for every character in
// it, every frame, through the engine-to-C# bridge: every colored number on
// every card in hand, intent, power and tooltip, all the time. Built-in
// [color=#EFC851] is drawn natively and only when the text changes.
//
// The text is converted where the game sets it (MegaRichTextLabel.
// SetTextAutoSize), and labels already on screen when this is turned on.
// Only color spans with no other color tag inside or around them: with the
// game's effects the outer color wins, with [color] the inner one, so nested
// ones stay as the game wrote them. Animated effects ([sine], [jitter],
// fades) stay effects. Colors come from the game's StsColors; if they or the
// label class aren't there (a game update), nothing changes.
//
// Turned off, new text is left as the game writes it; text already converted
// stays converted until the game next sets it.
internal static class TextColors
{
    private const string HarmonyId = "com.sts2perf.textcolors";
    private static readonly string[] Names = { "gold", "blue", "red", "green", "aqua", "orange", "pink", "purple" };

    private static Harmony _harmony;
    private static bool _on;
    private static bool _unavailable;
    private static Dictionary<string, string> _open;
    private static Type _labelType;
    private static int _converted;

    // From PerfRuntime.ApplyAll once the game's UI is up.
    public static void Apply()
    {
        var on = PerfSettings.Value("text_fx") == "native";
        if (on == _on || _unavailable)
            return;
        if (on)
        {
            if (!Prepare(out var setText))
            {
                _unavailable = true;
                PerfEntry.Log("text colors: the game's labels or colors aren't as expected in this game version, left as the game does it");
                return;
            }
            _harmony ??= new Harmony(HarmonyId);
            _harmony.Patch(setText, prefix: new HarmonyMethod(typeof(TextColors), nameof(ConvertArgument)));
            var swept = Sweep();
            PerfEntry.Log($"text colors: built-in ({swept} labels on screen converted, new text as it's set)");
        }
        else
        {
            _harmony.UnpatchAll(HarmonyId);
            PerfEntry.Log("text colors: as the game (text already converted stays so until the game sets it again)");
        }
        _on = on;
    }

    private static bool Prepare(out MethodInfo setText)
    {
        _labelType = AccessTools.TypeByName("MegaCrit.Sts2.addons.mega_text.MegaRichTextLabel");
        setText = _labelType == null ? null : AccessTools.Method(_labelType, "SetTextAutoSize", new[] { typeof(string) });
        var colors = AccessTools.TypeByName("MegaCrit.Sts2.Core.Helpers.StsColors");
        if (setText == null || colors == null)
            return false;
        _open = new Dictionary<string, string>();
        foreach (var name in Names)
        {
            if (AccessTools.Field(colors, name)?.GetValue(null) is not Color color)
                return false;
            _open[name] = $"[color=#{color.ToHtml(false)}]";
        }
        return true;
    }

    private static void ConvertArgument(ref string text)
    {
        try
        {
            text = Convert(text);
        }
        catch
        {
            // Never let this break the game's text.
        }
    }

    // Labels already on screen.
    private static int Sweep()
    {
        var count = 0;
        if (Engine.GetMainLoop() is not SceneTree tree)
            return 0;
        var stack = new Stack<Node>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (_labelType.IsInstanceOfType(node))
            {
                var text = node.Get("text").AsString();
                var converted = Convert(text);
                if (!ReferenceEquals(converted, text))
                {
                    node.Set("text", converted);
                    count++;
                }
            }
            foreach (var child in node.GetChildren())
                stack.Push(child);
        }
        return count;
    }

    // "[gold]6[/gold] damage" -> "[color=#efc851]6[/color] damage", for color
    // spans with no other color tag inside or around them. The same string
    // back when there's nothing to convert.
    internal static string Convert(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('[') < 0)
            return text;
        // Color tags in order: (position, length, name, closing).
        var tags = new List<(int At, int Length, string Name, bool Close)>();
        for (var i = text.IndexOf('['); i >= 0; i = text.IndexOf('[', i + 1))
        {
            var end = text.IndexOf(']', i);
            if (end < 0)
                break;
            var close = text[i + 1] == '/';
            var name = text.Substring(i + (close ? 2 : 1), end - i - (close ? 2 : 1));
            if (_open.ContainsKey(name))
                tags.Add((i, end - i + 1, name, close));
        }
        if (tags.Count == 0)
            return text;
        // Pair them up; a top-level span is converted only if nothing nests.
        var convert = new List<(int Open, int Close)>();
        var depth = 0;
        var spanStart = -1;
        var nested = false;
        for (var t = 0; t < tags.Count; t++)
        {
            var tag = tags[t];
            if (!tag.Close)
            {
                if (depth == 0)
                {
                    spanStart = t;
                    nested = false;
                }
                else
                {
                    nested = true;
                }
                depth++;
            }
            else
            {
                if (depth == 0)
                    return text; // unbalanced: leave it to the game
                depth--;
                if (depth == 0 && !nested && tags[spanStart].Name == tag.Name)
                    convert.Add((spanStart, t));
            }
        }
        if (depth != 0 || convert.Count == 0)
            return text;
        var result = new StringBuilder(text.Length + convert.Count * 16);
        var at = 0;
        foreach (var (open, close) in convert)
        {
            var o = tags[open];
            var c = tags[close];
            result.Append(text, at, o.At - at).Append(_open[o.Name]);
            result.Append(text, o.At + o.Length, c.At - (o.At + o.Length)).Append("[/color]");
            at = c.At + c.Length;
        }
        result.Append(text, at, text.Length - at);
        _converted++;
        return result.ToString();
    }

    // For FrameLog's stats line: texts converted in the window.
    public static string TakeWindowSummary()
    {
        if (!_on)
            return "";
        var text = $" | text colors {_converted} converted";
        _converted = 0;
        return text;
    }
}
