using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace STS2Perf;

// Turns the method names Mono's JIT reports (JitEvents) back into methods
// that can be compiled ahead of time (JitWarmup). Plain reflection, no Godot,
// so it can be tested on desktop Mono.
//
// Names come in two spellings. The method's type (MethodNamespace) joins
// nested types with '.', and a generic instance spells out its arguments
// without arity: "System.Runtime.CompilerServices.AsyncTaskMethodBuilder.
// AsyncStateMachineBox<System.Int32,MegaCrit.Foo.<PlayAsync>d__7>". The
// signature (MethodSignature) joins nested types with '/', keeps the arity,
// and uses C# keywords: "void(MegaCrit.Foo/<PlayAsync>d__7&)",
// "System.Threading.Tasks.Task`1<int>(int)".
//
// A generic method's instance (AsyncMethodBuilderCore.Start<TStateMachine>,
// which runs every async method) has no type arguments in its name; they are
// read off the signature's parameter types.
internal static class JitNames
{
    private const BindingFlags Declared =
        BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // Never recorded: the launcher and its patching, Steam, crash reporting,
    // runtime wrappers and dynamic methods.
    private static readonly string[] Never =
    {
        "STS2Mobile", "STS2Perf", "HarmonyLib", "MonoMod", "Steamworks", "Sentry", "DMD<", "(wrapper", "<Module>",
    };

    // The runtime and libraries: recorded only when instantiated over the
    // game's own types (async state machines, collections of game objects),
    // since everything else compiled before the game started.
    private static readonly string[] Library = { "System.", "Microsoft.", "Mono.", "Internal.", "Interop" };

    private static readonly Dictionary<string, Type> Aliases = new()
    {
        ["void"] = typeof(void), ["bool"] = typeof(bool), ["char"] = typeof(char), ["sbyte"] = typeof(sbyte),
        ["byte"] = typeof(byte), ["short"] = typeof(short), ["ushort"] = typeof(ushort), ["int"] = typeof(int),
        ["uint"] = typeof(uint), ["long"] = typeof(long), ["ulong"] = typeof(ulong), ["single"] = typeof(float),
        ["float"] = typeof(float), ["double"] = typeof(double), ["string"] = typeof(string), ["object"] = typeof(object),
        ["intptr"] = typeof(IntPtr), ["uintptr"] = typeof(UIntPtr), ["native int"] = typeof(IntPtr),
        ["native uint"] = typeof(UIntPtr), ["decimal"] = typeof(decimal),
    };

    public static bool ShouldRecord(string type, string method, string signature)
    {
        if (method == ".cctor" || type.Length == 0 || type.Contains("__Canon") || (signature ?? "").Contains("__Canon"))
            return false;
        if (Never.Any(p => type.StartsWith(p, StringComparison.Ordinal)))
            return false;
        if (Library.Any(p => type.StartsWith(p, StringComparison.Ordinal)))
            return type.Contains("MegaCrit.") || (signature ?? "").Contains("MegaCrit.");
        return true;
    }

    public sealed class Resolver
    {
        private readonly Assembly[] _assemblies;
        private readonly Dictionary<string, Type> _cache = new();

        public Resolver(Assembly[] assemblies) => _assemblies = assemblies;

        // A type from either spelling; null when it can't be found or built.
        public Type Type(string name)
        {
            name = name.Trim();
            if (name.Length == 0)
                return null;
            if (_cache.TryGetValue(name, out var cached))
                return cached;
            Type found = null;
            try
            {
                found = Build(name);
            }
            catch (Exception)
            {
                // A constraint violation or a missing assembly: not warmable.
            }
            _cache[name] = found;
            return found;
        }

        private Type Build(string name)
        {
            if (name.EndsWith("&", StringComparison.Ordinal))
                return Type(name[..^1]);
            if (name.EndsWith("[]", StringComparison.Ordinal))
                return Type(name[..^2])?.MakeArrayType();
            if (name.EndsWith("*", StringComparison.Ordinal))
                return null;
            if (Aliases.TryGetValue(name, out var alias))
                return alias;
            if (name.EndsWith(">", StringComparison.Ordinal))
            {
                var open = MatchingOpen(name);
                if (open > 0)
                {
                    var args = SplitTopLevel(name[(open + 1)..^1]).Select(Type).ToArray();
                    if (args.Any(a => a == null))
                        return null;
                    var definition = GenericDefinition(name[..open], args.Length);
                    return definition?.MakeGenericType(args);
                }
            }
            return Nested(name);
        }

        // "A.B.C.D" may be namespace A.B with C+D nested: try the splits from
        // the right. '/' (signature spelling) is always a nesting.
        private Type Nested(string name)
        {
            var candidate = name.Replace('/', '+');
            for (var tries = 0; tries < 4; tries++)
            {
                foreach (var assembly in _assemblies)
                {
                    var t = assembly.GetType(candidate, throwOnError: false);
                    if (t != null)
                        return t;
                }
                var dot = candidate.LastIndexOf('.');
                if (dot < 0)
                    return null;
                candidate = candidate[..dot] + "+" + candidate[(dot + 1)..];
            }
            return null;
        }

        // The open generic type for a base name and argument count. With the
        // arity spelled out ("Task`1") it's a plain lookup; without it, the
        // arguments are spread over the last one to three nested names.
        private Type GenericDefinition(string baseName, int count)
        {
            if (baseName.Contains('`'))
                return Nested(baseName);
            var parts = baseName.Split('.');
            for (var nestedCount = 1; nestedCount <= Math.Min(3, parts.Length); nestedCount++)
            {
                var ns = string.Join(".", parts.Take(parts.Length - nestedCount));
                var nested = parts.Skip(parts.Length - nestedCount).ToArray();
                foreach (var arities in Spreads(count, nestedCount))
                {
                    var chain = string.Join("+", nested.Select((n, i) => arities[i] > 0 ? $"{n}`{arities[i]}" : n));
                    var candidate = ns.Length > 0 ? $"{ns}.{chain}" : chain;
                    foreach (var assembly in _assemblies)
                    {
                        var t = assembly.GetType(candidate, throwOnError: false);
                        if (t != null && t.IsGenericTypeDefinition && t.GetGenericArguments().Length == count)
                            return t;
                    }
                }
            }
            return null;
        }

        // The methods an entry names, ready to compile: every non-generic
        // overload with that name, or a generic method instantiated with the
        // types its signature shows.
        public List<MethodBase> Methods(Type type, string name, string signature)
        {
            var found = new List<MethodBase>();
            if (name == ".ctor")
            {
                found.AddRange(type.GetConstructors(Declared & ~BindingFlags.Static).Where(c => !c.ContainsGenericParameters && HasBody(c)));
                return found;
            }
            var named = type.GetMethods(Declared).Where(m => m.Name == name && HasBody(m)).ToList();
            found.AddRange(named.Where(m => !m.IsGenericMethodDefinition && !m.ContainsGenericParameters));
            var generic = named.Where(m => m.IsGenericMethodDefinition).ToList();
            if (generic.Count == 0 || string.IsNullOrEmpty(signature))
                return found;
            var parameters = ParameterNames(signature);
            if (parameters == null)
                return found;
            foreach (var definition in generic)
            {
                var instance = Instantiate(definition, parameters);
                if (instance != null)
                    found.Add(instance);
            }
            return found;
        }

        // Binds the method's type parameters by matching each declared
        // parameter type against the signature's: Start<T>(ref T) with
        // "Foo/<Bar>d__1&" gives T = Foo+<Bar>d__1, Select<T, R>(
        // IEnumerable<T>, Func<T, R>) with "IEnumerable`1<A>,Func`2<A, B>"
        // gives T = A, R = B.
        private MethodInfo Instantiate(MethodInfo definition, List<string> parameterNames)
        {
            var parameters = definition.GetParameters();
            if (parameters.Length != parameterNames.Count)
                return null;
            var bindings = new Type[definition.GetGenericArguments().Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var declared = parameters[i].ParameterType;
                if (declared.IsByRef)
                    declared = declared.GetElementType();
                if (!declared.ContainsGenericParameters)
                    continue;
                var actual = Type(parameterNames[i]);
                if (actual == null || !Unify(declared, actual, bindings))
                    return null;
            }
            if (bindings.Any(b => b == null))
                return null;
            try
            {
                return definition.MakeGenericMethod(bindings);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private static bool Unify(Type declared, Type actual, Type[] bindings)
    {
        if (declared.IsGenericParameter)
        {
            if (declared.DeclaringMethod == null)
                return true;
            var slot = declared.GenericParameterPosition;
            if (bindings[slot] != null && bindings[slot] != actual)
                return false;
            bindings[slot] = actual;
            return true;
        }
        if (!declared.ContainsGenericParameters)
            return true;
        if (declared.IsArray || declared.IsByRef || declared.IsPointer)
            return actual.HasElementType && declared.IsArray == actual.IsArray
                && Unify(declared.GetElementType(), actual.GetElementType(), bindings);
        if (!declared.IsGenericType || !actual.IsGenericType || actual.GetGenericTypeDefinition() != declared.GetGenericTypeDefinition())
            return false;
        var want = declared.GetGenericArguments();
        var have = actual.GetGenericArguments();
        for (var i = 0; i < want.Length; i++)
            if (!Unify(want[i], have[i], bindings))
                return false;
        return true;
    }

    // Methods with IL to compile: not abstract, not P/Invoke, not provided
    // by the runtime (delegate Invoke, internal calls).
    private static bool HasBody(MethodBase method) =>
        !method.IsAbstract
        && (method.Attributes & MethodAttributes.PinvokeImpl) == 0
        && (method.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0;

    // "ret(a,b<c,d>,e&)" -> ["a", "b<c,d>", "e&"]; null if malformed.
    public static List<string> ParameterNames(string signature)
    {
        var depth = 0;
        for (var i = 0; i < signature.Length; i++)
        {
            switch (signature[i])
            {
                case '<':
                case '[':
                    depth++;
                    break;
                case '>':
                case ']':
                    depth--;
                    break;
                case '(' when depth == 0:
                    if (!signature.EndsWith(")", StringComparison.Ordinal))
                        return null;
                    var inner = signature[(i + 1)..^1];
                    return inner.Length == 0 ? new List<string>() : SplitTopLevel(inner);
            }
        }
        return null;
    }

    private static List<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            switch (list[i])
            {
                case '<':
                case '[':
                    depth++;
                    break;
                case '>':
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(list[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        parts.Add(list[start..].Trim());
        return parts;
    }

    // Index of the '<' that the final '>' closes, or -1. A compiler-generated
    // name ("<PlayAsync>d__7") doesn't end in '>', so it never gets here.
    private static int MatchingOpen(string name)
    {
        var depth = 0;
        for (var i = name.Length - 1; i >= 0; i--)
        {
            if (name[i] == '>')
                depth++;
            else if (name[i] == '<' && --depth == 0)
                return i;
        }
        return -1;
    }

    // Every way to spread `total` generic arguments over `slots` nested names.
    private static IEnumerable<int[]> Spreads(int total, int slots)
    {
        if (slots == 1)
        {
            yield return new[] { total };
            yield break;
        }
        for (var first = 0; first <= total; first++)
            foreach (var rest in Spreads(total - first, slots - 1))
                yield return new[] { first }.Concat(rest).ToArray();
    }
}
