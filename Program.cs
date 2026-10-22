using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace PclDownloadUnlocker;

internal static class Program
{
    private const string TargetFileName = "Plain Craft Launcher 2.exe";
    private const string DonorResourceName = "PclDownloadUnlocker.Assets.PCL.2.14.0.0.exe";
    private const string SupportedDonorSha256 = "5582952DED3B443503F66C23B810802BD20F1A3E847B54D9E09653A825859A36";
    private const string ModificationNotice = "程序经修改";

    private static readonly string[] GraftedMethods =
    [
        "PCL.ModModpack::ModpackInstall System.Void ()",
        "PCL.ModModpack::ModpackInstall PCL.ModLoader/LoaderCombo`1<System.String> (System.String,System.String,System.String)",
        "PCL.PageDownloadInstall::LoaderInit System.Void ()",
        "PCL.PageDownloadModify::LoadInstanceFinished System.Void (PCL.ModLoader/LoaderTask`2<PCL.ModMinecraft/McInstance,System.Object>)",
        "PCL.PageDownloadResourceDetail::InitializeStackResourceVersions System.Void (System.Windows.Controls.StackPanel)"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                PrintUsage();
                return 0;
            }

            var noLaunch = args.Contains("--no-launch", StringComparer.OrdinalIgnoreCase);
            var positional = args.Where(a => !a.StartsWith('-')).ToArray();
            var targetPath = ResolveTargetPath(positional.FirstOrDefault());

            Console.WriteLine("PCL 下载功能外置补丁（第三方独立作品，非 PCL 官方提供）");
            Console.WriteLine($"目标：{targetPath}");
            var originalHash = ComputeSha256(targetPath);
            Console.WriteLine($"原文件 SHA-256：{originalHash}");

            string targetAssemblyName;
            Version? targetAssemblyVersion;
            using (var inspectionModule = ModuleDefMD.Load(targetPath))
            {
                ValidateTarget(inspectionModule);
                targetAssemblyName = inspectionModule.Assembly?.Name.String
                    ?? throw new InvalidDataException("目标文件没有程序集名称。");
                targetAssemblyVersion = inspectionModule.Assembly?.Version;
                Console.WriteLine($"识别到兼容的 PCL 程序集：{targetAssemblyName} {targetAssemblyVersion}");
            }

            var donorBytes = GetEmbeddedDonorBytes();
            var outputPath = Path.Combine(
                Path.GetDirectoryName(targetPath)!,
                Path.GetFileNameWithoutExtension(targetPath) + ".DownloadUnlocked.exe");
            var temporaryPath = outputPath + ".tmp";

            using (var targetModule = ModuleDefMD.Load(targetPath))
            using (var donorModule = ModuleDefMD.Load(donorBytes))
            {
                foreach (var methodName in GraftedMethods)
                {
                    var source = FindMethod(donorModule, methodName);
                    var destination = FindMethod(targetModule, methodName);
                    CopyBody(source, destination, donorModule, targetModule);
                    Console.WriteLine($"已恢复：{methodName}");
                }
                AddModificationNotice(targetModule);
                Console.WriteLine($"已添加下载栏标识：{ModificationNotice}");

                var options = new ModuleWriterOptions(targetModule);
                options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
                targetModule.Write(temporaryPath, options);
            }

            File.Move(temporaryPath, outputPath, true);
            ValidateOutput(outputPath, donorBytes, targetAssemblyName, targetAssemblyVersion);
            var unchangedHash = ComputeSha256(targetPath);
            if (!unchangedHash.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("安全检查失败：原始 PCL 文件发生了变化。请立即从可信来源重新下载 PCL。");

            Console.WriteLine($"补丁副本：{outputPath}");

            if (!noLaunch)
            {
                Process.Start(new ProcessStartInfo(outputPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(targetPath)!
                });
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("补丁失败：" + ex);
            Console.Error.WriteLine("按回车键退出……");
            if (!Console.IsInputRedirected) Console.ReadLine();
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法：PCL.DownloadUnlocker.exe [PCL_EXE路径] [--no-launch]");
        Console.WriteLine("兼容性按程序集结构检测，可用于结构兼容的正式版和快照版。");
        Console.WriteLine("补丁完全离线运行，不会联网下载任何文件。");
        Console.WriteLine("补丁会生成同目录副本，不会覆盖或改写原始 PCL EXE。");
    }

    private static string ResolveTargetPath(string? targetArgument)
    {
        if (!string.IsNullOrWhiteSpace(targetArgument))
        {
            var explicitPath = Path.GetFullPath(targetArgument);
            if (!File.Exists(explicitPath))
                throw new FileNotFoundException($"未找到 PCL：{explicitPath}", explicitPath);
            return explicitPath;
        }

        var defaultPath = Path.Combine(AppContext.BaseDirectory, TargetFileName);
        if (File.Exists(defaultPath)) return Path.GetFullPath(defaultPath);

        var candidates = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.exe")
            .Where(path => !Path.GetFileName(path).Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).EndsWith(".DownloadUnlocked.exe", StringComparison.OrdinalIgnoreCase))
            .Where(IsPclCandidate)
            .ToArray();

        return candidates.Length switch
        {
            1 => Path.GetFullPath(candidates[0]),
            0 => throw new FileNotFoundException("同目录没有找到可识别的 PCL EXE。请把补丁放到 PCL 旁边，或把目标 EXE 拖到补丁上。"),
            _ => throw new InvalidOperationException("同目录找到了多个 PCL EXE。请把要处理的正式版或快照版 EXE 拖到补丁上。")
        };
    }

    private static bool IsPclCandidate(string path)
    {
        try
        {
            using var module = ModuleDefMD.Load(path);
            return module.GetTypes().Any(type => type.FullName == "PCL.PageDownloadInstall");
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateTarget(ModuleDef module)
    {
        if (module.Assembly is null ||
            !module.GetTypes().Any(type => type.FullName == "PCL.PageDownloadInstall") ||
            !module.GetTypes().Any(type => type.FullName == "PCL.ModModpack"))
            throw new InvalidDataException("目标不是可识别的 PCL2 程序集。");

        foreach (var methodName in GraftedMethods)
            _ = FindMethod(module, methodName);
    }

    private static byte[] GetEmbeddedDonorBytes()
    {
        Console.WriteLine("正在读取补丁内置的本地代码载体……");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DonorResourceName)
            ?? throw new InvalidDataException("补丁内置代码载体缺失。");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        var bytes = output.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(SupportedDonorSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("补丁内置代码载体校验失败，已中止补丁。");
        return bytes;
    }

    private static void AddModificationNotice(ModuleDef module)
    {
        var pageType = module.GetTypes().SingleOrDefault(type => type.FullName == "PCL.PageDownloadLeft")
            ?? throw new TypeLoadException("目标版本缺少下载栏类型：PCL.PageDownloadLeft");
        var constructor = pageType.Methods.SingleOrDefault(method => method.Name == ".ctor" && method.MethodSig.Params.Count == 0)
            ?? throw new MissingMethodException("目标版本缺少下载栏构造方法。");
        var getItem = pageType.Methods.SingleOrDefault(method => method.Name == "get_ItemInstall" && method.MethodSig.Params.Count == 0)
            ?? throw new MissingMethodException("目标版本缺少原版游戏下载栏入口。");
        var listItemType = module.GetTypes().SingleOrDefault(type => type.FullName == "PCL.MyListItem")
            ?? throw new TypeLoadException("目标版本缺少下载栏控件类型：PCL.MyListItem");
        var setInfo = listItemType.Methods.SingleOrDefault(method => method.Name == "set_Info" &&
            method.MethodSig.Params.Count == 1 && method.MethodSig.Params[0].FullName == "System.String")
            ?? throw new MissingMethodException("目标版本缺少下载栏说明文字属性。");
        if (!constructor.HasBody) throw new InvalidDataException("下载栏构造方法没有 CIL。");

        var instructions = constructor.Body.Instructions;
        var initializeIndex = instructions.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.OpCode.Code is Code.Call or Code.Callvirt)
            .Where(pair => pair.instruction.Operand is IMethod method && method.Name == "InitializeComponent")
            .Select(pair => pair.index)
            .FirstOrDefault(-1);
        if (initializeIndex < 0)
            throw new MissingMethodException("无法定位下载栏初始化位置。");

        var insertionIndex = initializeIndex + 1;
        instructions.Insert(insertionIndex++, Instruction.Create(OpCodes.Ldarg_0));
        instructions.Insert(insertionIndex++, Instruction.Create(OpCodes.Call, getItem));
        instructions.Insert(insertionIndex++, Instruction.Create(OpCodes.Ldstr, ModificationNotice));
        instructions.Insert(insertionIndex, Instruction.Create(OpCodes.Callvirt, setInfo));
    }

    private static MethodDef FindMethod(ModuleDef module, string methodKey)
        => module.GetTypes().SelectMany(t => t.Methods).SingleOrDefault(m => GetMethodKey(m) == methodKey)
           ?? throw new MissingMethodException($"程序集里找不到预期方法：{methodKey}");

    private static string GetMethodKey(MethodDef method)
        => $"{method.DeclaringType.FullName}::{method.Name} {method.MethodSig}";

    private static void ValidateOutput(
        string outputPath,
        byte[] donorBytes,
        string targetAssemblyName,
        Version? targetAssemblyVersion)
    {
        using var output = ModuleDefMD.Load(outputPath);
        using var donor = ModuleDefMD.Load(donorBytes);
        if (output.Assembly?.Name.String != targetAssemblyName || output.Assembly?.Version != targetAssemblyVersion)
            throw new InvalidDataException("输出程序集标识或版本与原文件不一致。");
        if (output.GetAssemblyRefs().Any(reference => reference.Name == output.Assembly?.Name))
            throw new InvalidDataException("输出程序集出现了无效的自引用。");

        foreach (var methodName in GraftedMethods)
        {
            var actual = FindMethod(output, methodName);
            var expected = FindMethod(donor, methodName);
            if (!actual.HasBody || actual.Body.Instructions.Count != expected.Body.Instructions.Count ||
                actual.Body.ExceptionHandlers.Count != expected.Body.ExceptionHandlers.Count)
                throw new InvalidDataException($"输出方法校验失败：{methodName}");
            if (actual.Body.Instructions.Any(instruction => instruction.OpCode == OpCodes.Ldstr &&
                    instruction.Operand is string text && text.Contains("暂时不再提供 MC 下载功能")))
                throw new InvalidDataException($"输出方法仍含下载限制：{methodName}");
        }

        var downloadLeft = output.GetTypes().SingleOrDefault(type => type.FullName == "PCL.PageDownloadLeft");
        var constructor = downloadLeft?.Methods.SingleOrDefault(method => method.Name == ".ctor" && method.MethodSig.Params.Count == 0);
        if (constructor?.Body?.Instructions.Any(instruction =>
                instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, ModificationNotice)) != true)
            throw new InvalidDataException("输出文件缺少“程序经修改”下载栏标识。");

        Console.WriteLine("结构校验：通过");
    }

    private static void CopyBody(MethodDef source, MethodDef destination, ModuleDef donorModule, ModuleDef targetModule)
    {
        if (!source.HasBody) throw new InvalidDataException($"来源方法没有 CIL：{source.FullName}");
        var importer = new Importer(targetModule, ImporterOptions.TryToUseTypeDefs);
        var sourceBody = source.Body;
        var targetBody = new CilBody
        {
            InitLocals = sourceBody.InitLocals,
            KeepOldMaxStack = sourceBody.KeepOldMaxStack,
            MaxStack = sourceBody.MaxStack,
            HeaderSize = sourceBody.HeaderSize
        };

        var localMap = new Dictionary<Local, Local>();
        foreach (var local in sourceBody.Variables)
        {
            var cloned = new Local(importer.Import(local.Type)) { Name = local.Name };
            targetBody.Variables.Add(cloned);
            localMap.Add(local, cloned);
        }

        var instructionMap = sourceBody.Instructions.ToDictionary(i => i, i => new Instruction(i.OpCode));
        foreach (var sourceInstruction in sourceBody.Instructions)
        {
            var targetInstruction = instructionMap[sourceInstruction];
            targetInstruction.Operand = MapOperand(sourceInstruction.Operand, source, destination, donorModule, targetModule, importer, localMap, instructionMap);
            targetBody.Instructions.Add(targetInstruction);
        }

        foreach (var handler in sourceBody.ExceptionHandlers)
        {
            targetBody.ExceptionHandlers.Add(new ExceptionHandler(handler.HandlerType)
            {
                TryStart = MapInstruction(handler.TryStart, instructionMap),
                TryEnd = MapInstruction(handler.TryEnd, instructionMap),
                HandlerStart = MapInstruction(handler.HandlerStart, instructionMap),
                HandlerEnd = MapInstruction(handler.HandlerEnd, instructionMap),
                FilterStart = MapInstruction(handler.FilterStart, instructionMap),
                CatchType = handler.CatchType is null ? null : MapType(handler.CatchType, donorModule, targetModule, importer)
            });
        }

        destination.Body = targetBody;
    }

    private static object? MapOperand(
        object? operand,
        MethodDef sourceMethod,
        MethodDef targetMethod,
        ModuleDef donorModule,
        ModuleDef targetModule,
        Importer importer,
        IReadOnlyDictionary<Local, Local> localMap,
        IReadOnlyDictionary<Instruction, Instruction> instructionMap)
        => operand switch
        {
            null => null,
            Instruction instruction => instructionMap[instruction],
            IList<Instruction> instructions => instructions.Select(i => instructionMap[i]).ToArray(),
            Local local => localMap[local],
            Parameter parameter => targetMethod.Parameters[sourceMethod.Parameters.IndexOf(parameter)],
            MethodSpec methodSpec => MapMethodSpec(methodSpec, donorModule, targetModule, importer),
            IMethod method => MapMethod(method, donorModule, targetModule, importer),
            IField field => MapField(field, donorModule, targetModule, importer),
            ITypeDefOrRef type => MapType(type, donorModule, targetModule, importer),
            MethodSig signature => importer.Import(signature),
            _ => operand
        };

    private static Instruction? MapInstruction(Instruction? instruction, IReadOnlyDictionary<Instruction, Instruction> map)
        => instruction is null ? null : map[instruction];

    private static ITypeDefOrRef MapType(ITypeDefOrRef type, ModuleDef donorModule, ModuleDef targetModule, Importer importer)
    {
        var resolved = type.ResolveTypeDef();
        if (resolved?.Module == donorModule)
            return targetModule.GetTypes().SingleOrDefault(t => t.FullName == resolved.FullName)
                   ?? throw new TypeLoadException($"目标版本缺少来源类型：{resolved.FullName}");
        return importer.Import(type);
    }

    private static IMethod MapMethod(IMethod method, ModuleDef donorModule, ModuleDef targetModule, Importer importer)
    {
        var resolved = method.ResolveMethodDef();
        if (resolved?.Module == donorModule)
        {
            var existing = targetModule.GetTypes().SelectMany(t => t.Methods).SingleOrDefault(m => m.FullName == resolved.FullName);
            if (existing is not null) return existing;
            var targetType = targetModule.GetTypes().SingleOrDefault(t => t.FullName == resolved.DeclaringType.FullName)
                ?? throw new TypeLoadException($"目标版本缺少来源方法的声明类型：{resolved.DeclaringType.FullName}");
            var created = new MethodDefUser(resolved.Name, importer.Import(resolved.MethodSig), resolved.ImplAttributes, resolved.Attributes);
            targetType.Methods.Add(created);
            CopyBody(resolved, created, donorModule, targetModule);
            return created;
        }
        return importer.Import(method);
    }

    private static IField MapField(IField field, ModuleDef donorModule, ModuleDef targetModule, Importer importer)
    {
        var resolved = field.ResolveFieldDef();
        if (resolved?.Module == donorModule)
        {
            var existing = targetModule.GetTypes().SelectMany(t => t.Fields).SingleOrDefault(f => f.FullName == resolved.FullName);
            if (existing is not null) return existing;
            var targetType = targetModule.GetTypes().SingleOrDefault(t => t.FullName == resolved.DeclaringType.FullName)
                ?? throw new TypeLoadException($"目标版本缺少来源字段的声明类型：{resolved.DeclaringType.FullName}");
            var created = new FieldDefUser(resolved.Name, importer.Import(resolved.FieldSig), resolved.Attributes);
            targetType.Fields.Add(created);
            return created;
        }
        return importer.Import(field);
    }

    private static IMethod MapMethodSpec(MethodSpec methodSpec, ModuleDef donorModule, ModuleDef targetModule, Importer importer)
    {
        var mappedMethod = (IMethodDefOrRef)MapMethod(methodSpec.Method, donorModule, targetModule, importer);
        var mappedArguments = methodSpec.GenericInstMethodSig.GenericArguments.Select(importer.Import).ToList();
        return new MethodSpecUser(mappedMethod, new GenericInstMethodSig(mappedArguments));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
