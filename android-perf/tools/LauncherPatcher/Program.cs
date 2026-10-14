// Patches the upstream launcher's STS2Mobile.dll for the perf build:
//
//   1. --string OLD NEW: replaces string literals (ldstr) that equal OLD.
//      Every OLD must be found at least once, so an upstream change can't
//      silently skip a replacement.
//   2. --addon FILE TYPE [--addon-dir DIR]: adds ModEntry.LoadPerfAddon(),
//      which loads DIR/FILE and calls TYPE.Apply(). DIR is where the app
//      unpacks assets/dotnet_bcl. If DIR/FILE doesn't exist it tries next to
//      STS2Mobile.dll, but on Android Assembly.Location isn't the real folder,
//      so the build passes DIR.
//      It runs inside ModEntry.Apply right after the Harmony instance is
//      created (after the TMPDIR fix MonoMod needs, before the first patch).
//      Any failure is caught and logged, so a broken add-on leaves the
//      launcher exactly as upstream ships it.
//
// Usage: LauncherPatcher <in.dll> <out.dll> [--string OLD NEW]... [--addon FILE TYPE] [--addon-dir DIR]

using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: LauncherPatcher <in.dll> <out.dll> [--string OLD NEW]... [--addon FILE TYPE] [--addon-dir DIR]");
    return 2;
}

var input = args[0];
var output = args[1];
var replacements = new List<(string Old, string New)>();
string? addonFile = null, addonType = null, addonDir = null;
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--string" when i + 2 < args.Length:
            replacements.Add((args[i + 1], args[i + 2]));
            i += 2;
            break;
        case "--addon" when i + 2 < args.Length:
            addonFile = args[i + 1];
            addonType = args[i + 2];
            i += 2;
            break;
        case "--addon-dir" when i + 1 < args.Length:
            addonDir = args[i + 1];
            i += 1;
            break;
        default:
            Console.Error.WriteLine($"unknown or incomplete option: {args[i]}");
            return 2;
    }
}

// Writing resolves the types of default parameter values (Godot enums), so
// let Cecil find STS2Mobile's references next to it (assets/dotnet_bcl).
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(input))!);
using var asm = AssemblyDefinition.ReadAssembly(
    new MemoryStream(File.ReadAllBytes(input)),
    new ReaderParameters { ReadSymbols = false, AssemblyResolver = resolver }
);
var module = asm.MainModule;

int failures = 0;

foreach (var (oldValue, newValue) in replacements)
{
    int hits = 0;
    foreach (var type in AllTypes(module))
    foreach (var method in type.Methods.Where(m => m.HasBody))
    foreach (var ins in method.Body.Instructions)
    {
        if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == oldValue)
        {
            ins.Operand = newValue;
            hits++;
            Console.WriteLine($"  string in {type.FullName}::{method.Name}");
        }
    }
    if (hits == 0)
    {
        Console.Error.WriteLine($"ERROR: string not found: {oldValue}");
        failures++;
    }
}

if (addonFile != null && addonType != null)
{
    if (!InjectAddonLoader(module, addonFile, addonType, addonDir ?? ""))
        failures++;
}

if (failures > 0)
    return 1;

asm.Write(output);
Console.WriteLine($"Wrote {output}");
return 0;

static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
{
    foreach (var t in module.Types)
    {
        yield return t;
        foreach (var n in Nested(t))
            yield return n;
    }

    static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
    {
        foreach (var n in t.NestedTypes)
        {
            yield return n;
            foreach (var nn in Nested(n))
                yield return nn;
        }
    }
}

static bool InjectAddonLoader(ModuleDefinition module, string addonFile, string addonType, string addonDir)
{
    var modEntry = module.GetType("STS2Mobile.ModEntry");
    var apply = modEntry?.Methods.FirstOrDefault(m => m.Name == "Apply" && m.IsStatic);
    if (apply == null)
    {
        Console.Error.WriteLine("ERROR: STS2Mobile.ModEntry.Apply not found");
        return false;
    }

    // Anchor: `RenderDiagnosticPatches.Apply(_harmony)`, the first patch call.
    var body = apply.Body.Instructions;
    var anchor = body.FirstOrDefault(i =>
        i.OpCode == OpCodes.Call
        && i.Operand is MethodReference mr
        && mr.Name == "Apply"
        && mr.DeclaringType.FullName == "STS2Mobile.Patches.RenderDiagnosticPatches"
    );
    if (anchor?.Previous is not { OpCode.Code: Code.Ldsfld } loadHarmony
        || ((FieldReference)loadHarmony.Operand).Name != "_harmony")
    {
        Console.Error.WriteLine("ERROR: anchor `RenderDiagnosticPatches.Apply(_harmony)` not found in ModEntry.Apply");
        return false;
    }

    AssemblyNameReference Ref(string name) =>
        module.AssemblyReferences.FirstOrDefault(a => a.Name == name)
        ?? throw new InvalidOperationException($"STS2Mobile.dll doesn't reference {name}");
    var runtime = Ref("System.Runtime");
    var console = Ref("System.Console");
    TypeReference T(string ns, string name, IMetadataScope scope, bool valueType = false) =>
        new(ns, name, module, scope, valueType);
    MethodReference M(TypeReference owner, string name, TypeReference ret, bool instance, params TypeReference[] ps)
    {
        var m = new MethodReference(name, ret, owner) { HasThis = instance };
        foreach (var p in ps)
            m.Parameters.Add(new ParameterDefinition(p));
        return m;
    }

    var ts = module.TypeSystem;
    var typeT = T("System", "Type", runtime);
    var assemblyT = T("System.Reflection", "Assembly", runtime);
    var methodInfoT = T("System.Reflection", "MethodInfo", runtime);
    var methodBaseT = T("System.Reflection", "MethodBase", runtime);
    var pathT = T("System.IO", "Path", runtime);
    var exceptionT = T("System", "Exception", runtime);
    var textWriterT = T("System.IO", "TextWriter", runtime);
    var consoleT = T("System", "Console", console);
    var handleT = T("System", "RuntimeTypeHandle", runtime, valueType: true);
    var fileT = T(
        "System.IO",
        "File",
        module.GetTypeReferences().FirstOrDefault(t => t.FullName == "System.IO.File")?.Scope ?? runtime
    );

    var loader = new MethodDefinition(
        "LoadPerfAddon",
        MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
        ts.Void
    )
    {
        NoInlining = true,
    };
    var exVar = new VariableDefinition(exceptionT);
    var pathVar = new VariableDefinition(ts.String);
    loader.Body.Variables.Add(exVar);
    loader.Body.Variables.Add(pathVar);
    loader.Body.InitLocals = true;
    var il = loader.Body.GetILProcessor();
    var end = il.Create(OpCodes.Ret);

    var combine = M(pathT, "Combine", ts.String, false, ts.String, ts.String);
    var writeLine = M(textWriterT, "WriteLine", ts.Void, true, ts.String);
    var concat = M(ts.String, "Concat", ts.String, false, ts.String, ts.String);
    var load = il.Create(OpCodes.Call, M(consoleT, "get_Error", textWriterT, false));

    // path = Path.Combine(DIR, FILE); if (!File.Exists(path))
    //     path = Path.Combine(Path.GetDirectoryName(typeof(ModEntry).Assembly.Location), FILE);
    var tryStart = il.Create(OpCodes.Ldstr, addonDir);
    il.Append(tryStart);
    il.Emit(OpCodes.Ldstr, addonFile);
    il.Emit(OpCodes.Call, combine);
    il.Emit(OpCodes.Stloc, pathVar);
    il.Emit(OpCodes.Ldloc, pathVar);
    il.Emit(OpCodes.Call, M(fileT, "Exists", ts.Boolean, false, ts.String));
    il.Emit(OpCodes.Brtrue, load);
    il.Emit(OpCodes.Ldtoken, modEntry);
    il.Emit(OpCodes.Call, M(typeT, "GetTypeFromHandle", typeT, false, handleT));
    il.Emit(OpCodes.Callvirt, M(typeT, "get_Assembly", assemblyT, true));
    il.Emit(OpCodes.Callvirt, M(assemblyT, "get_Location", ts.String, true));
    il.Emit(OpCodes.Call, M(pathT, "GetDirectoryName", ts.String, false, ts.String));
    il.Emit(OpCodes.Ldstr, addonFile);
    il.Emit(OpCodes.Call, combine);
    il.Emit(OpCodes.Stloc, pathVar);

    // Console.Error.WriteLine("[STS2Perf] loading " + path); Assembly.LoadFrom(path)...
    il.Append(load);
    il.Emit(OpCodes.Ldstr, "[STS2Perf] loading ");
    il.Emit(OpCodes.Ldloc, pathVar);
    il.Emit(OpCodes.Call, concat);
    il.Emit(OpCodes.Callvirt, writeLine);
    il.Emit(OpCodes.Ldloc, pathVar);
    il.Emit(OpCodes.Call, M(assemblyT, "LoadFrom", assemblyT, false, ts.String));
    il.Emit(OpCodes.Ldstr, addonType);
    il.Emit(OpCodes.Ldc_I4_1); // throwOnError
    il.Emit(OpCodes.Callvirt, M(assemblyT, "GetType", typeT, true, ts.String, ts.Boolean));
    il.Emit(OpCodes.Ldstr, "Apply");
    il.Emit(OpCodes.Callvirt, M(typeT, "GetMethod", methodInfoT, true, ts.String));
    il.Emit(OpCodes.Ldnull);
    il.Emit(OpCodes.Ldnull);
    il.Emit(OpCodes.Callvirt, M(methodBaseT, "Invoke", ts.Object, true, ts.Object, new ArrayType(ts.Object)));
    il.Emit(OpCodes.Pop);
    il.Emit(OpCodes.Leave, end);

    var handlerStart = il.Create(OpCodes.Stloc, exVar);
    il.Append(handlerStart);
    il.Emit(OpCodes.Call, M(consoleT, "get_Error", textWriterT, false));
    il.Emit(OpCodes.Ldstr, $"[STS2Perf] {addonFile} failed to load: ");
    il.Emit(OpCodes.Ldloc, exVar);
    il.Emit(OpCodes.Callvirt, M(ts.Object, "ToString", ts.String, true));
    il.Emit(OpCodes.Call, concat);
    il.Emit(OpCodes.Callvirt, writeLine);
    il.Emit(OpCodes.Leave, end);
    il.Append(end);

    loader.Body.ExceptionHandlers.Add(
        new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = tryStart,
            TryEnd = handlerStart,
            HandlerStart = handlerStart,
            HandlerEnd = end,
            CatchType = exceptionT,
        }
    );
    modEntry!.Methods.Add(loader);

    var call = Instruction.Create(OpCodes.Call, loader);
    apply.Body.GetILProcessor().InsertBefore(loadHarmony, call);
    // Anything that jumped to the anchor must run the loader too.
    foreach (var ins in body)
    {
        if (ins.Operand == loadHarmony && ins != call)
            ins.Operand = call;
    }
    foreach (var eh in apply.Body.ExceptionHandlers)
    {
        if (eh.TryStart == loadHarmony || eh.HandlerStart == loadHarmony || eh.FilterStart == loadHarmony)
        {
            Console.Error.WriteLine("ERROR: anchor is an exception-handler boundary; refusing to inject");
            return false;
        }
    }
    Console.WriteLine($"  injected ModEntry.LoadPerfAddon() -> {Path.Combine(addonDir, addonFile)} {addonType}.Apply()");
    return true;
}
