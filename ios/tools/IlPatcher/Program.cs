// Rewrites sts2.dll for iOS before NativeAOT compiles it. iOS has no JIT, so
// the Android launcher's Harmony patches (src/STS2Mobile/Patches) can't run
// there. The platform fixes they apply at runtime are applied here to the IL
// instead, plus a call into Sts2IosSupport.dll (ios/support) at .NET startup.
//
// Usage: IlPatcher <sts2.dll> <Sts2IosSupport.dll> <output sts2.dll>

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: IlPatcher <sts2.dll> <Sts2IosSupport.dll> <output.dll>");
    return 2;
}

var (inputPath, supportPath, outputPath) = (args[0], args[1], args[2]);
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(inputPath))!);
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(supportPath))!);

using var support = ModuleDefinition.ReadModule(supportPath, new ReaderParameters { AssemblyResolver = resolver });
using var module = ModuleDefinition.ReadModule(
    inputPath,
    new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, InMemory = true }
);

if (module.AssemblyReferences.Any(r => r.Name == support.Assembly.Name.Name))
{
    Console.Error.WriteLine($"{inputPath} already references {support.Assembly.Name.Name}; patch the original DLL.");
    return 1;
}

var hooks = support.GetType("Sts2Ios.Hooks") ?? throw new InvalidOperationException("Sts2Ios.Hooks not found");
MethodReference Hook(string name) => module.ImportReference(hooks.Methods.Single(m => m.Name == name));

var failures = 0;

// Same set as src/STS2Mobile/Patches/PlatformPatches.cs.
Patch("NGame", "InitializePlatform", required: true, m =>
    ReplaceBody(m, il =>
    {
        Note(il, "skipping Steam initialization");
        il.Emit(OpCodes.Call, Hook("TrueTask"));
        il.Emit(OpCodes.Ret);
    }, expectReturn: "System.Threading.Tasks.Task`1<System.Boolean>"));

Patch("OsDebugInfo", "LogSystemInfo", required: false, m => ReplaceBody(m, il => ReturnDefault(il, m)));

Patch("SentryService", "Initialize", required: false, m =>
    ReplaceBody(m, il =>
    {
        Note(il, "Sentry disabled");
        ReturnDefault(il, m);
    }));

Patch("PrefsSave", "get_UploadData", required: false, m =>
    ReplaceBody(m, il =>
    {
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }, expectReturn: "System.Boolean"));

Patch("GodotFileIo", "CreateDirectory", required: false, GuardCreateDirectory);

InjectRuntimeReady();

if (failures > 0)
{
    Console.Error.WriteLine($"{failures} required patch(es) failed; not writing output.");
    return 1;
}

module.Write(outputPath);
Console.WriteLine($"wrote {outputPath}");
return 0;

void Patch(string typeName, string methodName, bool required, Action<MethodDefinition> apply)
{
    var types = module.GetTypes().Where(t => t.Name == typeName).ToList();
    var methods = types.SelectMany(t => t.Methods).Where(m => m.Name == methodName && m.HasBody).ToList();
    if (methods.Count == 0)
    {
        var what = types.Count == 0 ? $"type {typeName}" : $"{typeName}.{methodName}";
        Console.WriteLine($"{(required ? "FAIL" : "skip")}: {what} not found");
        if (required)
            failures++;
        return;
    }
    foreach (var method in methods)
    {
        try
        {
            method.Body.SimplifyMacros();
            apply(method);
            method.Body.OptimizeMacros();
            Console.WriteLine($"ok:   {method.FullName}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"{(required ? "FAIL" : "skip")}: {method.FullName}: {e.Message}");
            if (required)
                failures++;
        }
    }
}

void ReplaceBody(MethodDefinition method, Action<ILProcessor> emit, string? expectReturn = null)
{
    if (expectReturn != null && method.ReturnType.FullName != expectReturn)
        throw new InvalidOperationException($"returns {method.ReturnType.FullName}, expected {expectReturn}");

    method.Body = new MethodBody(method);
    emit(method.Body.GetILProcessor());

    // The old body may have been an async/iterator stub; the attributes would
    // now point at a state machine the method no longer uses.
    foreach (var attr in method.CustomAttributes
                 .Where(a => a.AttributeType.Name is "AsyncStateMachineAttribute" or "IteratorStateMachineAttribute")
                 .ToList())
        method.CustomAttributes.Remove(attr);
}

void ReturnDefault(ILProcessor il, MethodDefinition method)
{
    switch (method.ReturnType.FullName)
    {
        case "System.Void":
            break;
        case "System.Boolean":
            il.Emit(OpCodes.Ldc_I4_0);
            break;
        case "System.Threading.Tasks.Task":
            il.Emit(OpCodes.Call, Hook("CompletedTask"));
            break;
        default:
            throw new NotSupportedException($"can't stub return type {method.ReturnType.FullName}");
    }
    il.Emit(OpCodes.Ret);
}

void Note(ILProcessor il, string message)
{
    il.Emit(OpCodes.Ldstr, message);
    il.Emit(OpCodes.Call, Hook("Note"));
}

// if (!Hooks.AllowCreateDirectory(this, directoryPath)) return;
void GuardCreateDirectory(MethodDefinition method)
{
    if (method.IsStatic || method.Parameters.Count != 1
        || method.Parameters[0].ParameterType.FullName != "System.String"
        || method.ReturnType.FullName != "System.Void")
        throw new InvalidOperationException("unexpected signature");

    var il = method.Body.GetILProcessor();
    var original = method.Body.Instructions[0];
    il.InsertBefore(original, il.Create(OpCodes.Ldarg_0));
    il.InsertBefore(original, il.Create(OpCodes.Ldarg_1));
    il.InsertBefore(original, il.Create(OpCodes.Call, Hook("AllowCreateDirectory")));
    il.InsertBefore(original, il.Create(OpCodes.Brtrue, original));
    il.InsertBefore(original, il.Create(OpCodes.Ret));
}

// Godot.SourceGenerators emits GodotPlugins.Game.Main.InitializeFromGameProject
// (exported as godotsharp_game_main_init) into the game assembly. Call
// Hooks.OnRuntimeReady right after it registers the game's script classes.
void InjectRuntimeReady()
{
    var main = module.GetTypes().FirstOrDefault(t => t.FullName == "GodotPlugins.Game.Main");
    var init = main?.Methods.FirstOrDefault(m => m.Name == "InitializeFromGameProject");
    var lookup = init?.Body.Instructions.FirstOrDefault(i =>
        i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "LookupScriptsInAssembly");
    if (lookup == null)
    {
        Console.WriteLine("FAIL: GodotPlugins.Game.Main.InitializeFromGameProject / LookupScriptsInAssembly not found");
        failures++;
        return;
    }
    var il = init!.Body.GetILProcessor();
    il.InsertAfter(lookup, il.Create(OpCodes.Call, Hook("OnRuntimeReady")));
    Console.WriteLine($"ok:   {init.FullName} -> Sts2Ios.Hooks.OnRuntimeReady");
}
