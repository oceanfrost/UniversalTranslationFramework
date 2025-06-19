using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using HarmonyLib;
using Verse;

namespace UniversalTranslationFramework
{
    /// <summary>
    /// Main entry point for the Universal Translation Framework - RimWorld Mod
    /// Compatible version without System.Collections.Immutable dependency
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TranslationFrameworkMod
    {
        public const string FRAMEWORK_HARMONY_ID = "Ocean.Universal.Translation.Framework";
        
        private static readonly Lazy<List<TranslationPatch>> _patches = 
            new Lazy<List<TranslationPatch>>(DiscoverAndLoadPatchesInternal);
        
        private static readonly ConcurrentDictionary<string, Type> _typeCache = 
            new ConcurrentDictionary<string, Type>();
        
        private static readonly ConcurrentDictionary<string, Assembly> _assemblyCache = 
            new ConcurrentDictionary<string, Assembly>();
        
        private static readonly object _initLock = new object();
        private static volatile bool _initialized = false;
        private static Harmony _harmony;

        static TranslationFrameworkMod()
        {
            // Use LongEventHandler for asynchronous initialization to avoid blocking game startup
            LongEventHandler.QueueLongEvent(() => InitializeFramework(), "Loading Universal Translation Framework", false, null);
        }

        private static void InitializeFramework()
        {
            if (_initialized) return;
            
            lock (_initLock)
            {
                if (_initialized) return;
                
                try
                {
                    Log.Message("[UTF] Universal Translation Framework is starting...");
                    
                    _harmony = new Harmony(FRAMEWORK_HARMONY_ID);
                    
                    // Trigger lazy loading
                    var patches = _patches.Value;
                    ApplyAllPatches(patches);
                    
                    // Log.Message($"[UTF] Universal Translation Framework started successfully! Loaded {patches.Count} translation patches.");
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    Log.Error($"[UTF] Failed to start Universal Translation Framework: {ex}");
                }
            }
        }

        /// <summary>
        /// Automatically discover and load translation patches from all mods (optimized version)
        /// </summary>
        private static List<TranslationPatch> DiscoverAndLoadPatchesInternal()
        {
            var discoveredPatches = new List<TranslationPatch>();
            var lockObject = new object();

            try
            {
                // Check if parallel processing is supported
                if (Environment.ProcessorCount > 1)
                {
                    var parallelOptions = new ParallelOptions 
                    { 
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
                    };

                    // Parallel mod scanning
                    Parallel.ForEach(LoadedModManager.RunningMods, parallelOptions, mod =>
                    {
                        var modPatches = ScanModForPatches(mod);
                        lock (lockObject)
                        {
                            discoveredPatches.AddRange(modPatches);
                        }
                    });
                }
                else
                {
                    // Single-threaded processing (compatibility fallback)
                    foreach (var mod in LoadedModManager.RunningMods)
                    {
                        var modPatches = ScanModForPatches(mod);
                        discoveredPatches.AddRange(modPatches);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] Error during parallel processing, falling back to sequential: {ex.Message}");
                
                // Fallback to single-threaded processing
                discoveredPatches.Clear();
                foreach (var mod in LoadedModManager.RunningMods)
                {
                    try
                    {
                        var modPatches = ScanModForPatches(mod);
                        discoveredPatches.AddRange(modPatches);
                    }
                    catch (Exception modEx)
                    {
                        Log.Error($"[UTF] Error scanning mod {mod.Name}: {modEx.Message}");
                    }
                }
            }

            return discoveredPatches;
        }

        private static List<TranslationPatch> ScanModForPatches(ModContentPack mod)
        {
            var patches = new List<TranslationPatch>();
            var patchesDir = Path.Combine(mod.RootDir, "Patches");
            
            if (!Directory.Exists(patchesDir))
                return patches;

            try
            {
                // Log.Message($"[UTF] Scanning Patches directory of mod '{mod.Name}': {patchesDir}");
                
                // Use EnumerateFiles to avoid loading all files into memory at once
                var translationFiles = Directory.EnumerateFiles(patchesDir, "*StringTranslation*.xml", 
                    SearchOption.AllDirectories);

                foreach (var filePath in translationFiles)
                {
                    try
                    {
                        var filePatches = LoadTranslationPatchesFromFileOptimized(filePath, mod);
                        patches.AddRange(filePatches);
                        
                        Log.Message($"[UTF] Loaded {filePatches.Count} translation patches from {Path.GetFileName(filePath)}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[UTF] Failed to load translation patch file {filePath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] Error occurred while scanning mod '{mod.Name}': {ex.Message}");
            }

            return patches;
        }

        /// <summary>
        /// Load translation patches from an XML file (optimized version)
        /// </summary>
        private static List<TranslationPatch> LoadTranslationPatchesFromFileOptimized(string filePath, ModContentPack mod)
        {
            var patches = new List<TranslationPatch>();
            
            try
            {
                // Use safer XML loading method
                XDocument doc;
                try
                {
                    using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192))
                    using (var reader = XmlReader.Create(fileStream, new XmlReaderSettings
                    {
                        IgnoreWhitespace = true,
                        IgnoreComments = true,
                        IgnoreProcessingInstructions = true,
                        DtdProcessing = DtdProcessing.Ignore
                    }))
                    {
                        doc = XDocument.Load(reader);
                    }
                }
                catch (Exception)
                {
                    // Fallback to simple loading method
                    doc = XDocument.Load(filePath);
                }

                var root = doc.Root;

                if (root?.Name != "Patch")
                {
                    Log.Warning($"[UTF] Invalid patch file format {filePath}: Root element must be <Patch>");
                    return patches;
                }

                // Find related operations
                var operations = root.Elements("Operation")
                    .Where(op => op.Attribute("Class")?.Value == "UniversalTranslationFramework.PatchOperationStringTranslate");

                foreach (var operation in operations)
                {
                    var patch = ParseStringTranslationPatchOptimized(operation, mod, filePath);
                    if (patch != null)
                    {
                        patches.Add(patch);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] XML parse error {filePath}: {ex.Message}");
            }

            return patches;
        }

        /// <summary>
        /// Parse a single string translation patch operation (optimized version)
        /// </summary>
        private static TranslationPatch ParseStringTranslationPatchOptimized(XElement operation, ModContentPack mod, string sourceFile)
        {
            try
            {
                var targetAssembly = operation.Element("targetAssembly")?.Value;
                var targetType = operation.Element("targetType")?.Value;
                var targetMethod = operation.Element("targetMethod")?.Value;
                
                if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(targetMethod))
                {
                    Log.Warning($"[UTF] Skipping invalid patch operation: Missing targetType or targetMethod ({sourceFile})");
                    return null;
                }

                var replacementsElement = operation.Element("replacements");
                if (replacementsElement == null)
                    return null;

                var items = replacementsElement.Elements("li").ToList();
                var replacements = new List<StringTranslation>(items.Count);

                foreach (var item in items)
                {
                    var original = item.Element("find")?.Value;
                    var translated = item.Element("replace")?.Value;

                    if (!string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(translated))
                    {
                        var translation = new StringTranslation
                        {
                            OriginalText = original,
                            TranslatedText = translated
                        };

                        // Check for format string attributes
                        var isFormatString = item.Attribute("isFormatString")?.Value;
                        if (bool.TryParse(isFormatString, out bool formatFlag))
                        {
                            translation.IsFormatString = formatFlag;
                        }
                        else
                        {
                            // Auto-detect format strings
                            translation.IsFormatString = FormatStringUtils.ContainsFormatPlaceholders(original);
                        }

                        // Check for regex attributes
                        var isRegex = item.Attribute("isRegex")?.Value;
                        if (bool.TryParse(isRegex, out bool regexFlag))
                        {
                            translation.IsRegex = regexFlag;
                        }

                        // Set pattern for regex translations
                        if (translation.IsRegex)
                        {
                            translation.Pattern = item.Attribute("pattern")?.Value ?? original;
                        }

                        // Validate format string compatibility
                        if (translation.IsFormatString)
                        {
                            if (!FormatStringUtils.ValidatePlaceholderCompatibility(original, translated))
                            {
                                Log.Warning($"[UTF] Format string placeholder mismatch in {sourceFile}: '{original}' -> '{translated}'");
                            }
                        }

                        // Set context if provided
                        translation.Context = item.Attribute("context")?.Value;

                        replacements.Add(translation);
                    }
                }

                if (replacements.Count > 0)
                {
                    return new TranslationPatch
                    {
                        SourceMod = mod,
                        SourceFile = sourceFile,
                        TargetAssembly = targetAssembly,
                        TargetTypeName = targetType,
                        TargetMethodName = targetMethod,
                        Translations = replacements
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] Failed to parse translation patch operation ({sourceFile}): {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Apply all discovered translation patches (optimized version)
        /// </summary>
        private static void ApplyAllPatches(List<TranslationPatch> patches)
        {
            var appliedCount = 0;
            var failedCount = 0;

            // Group by type to reduce redundant type lookups
            var patchesByType = patches.GroupBy(p => p.TargetTypeName).ToList();            foreach (var typeGroup in patchesByType)
            {
                try
                {
                    // Use enhanced type lookup, support for state machine types
                    var firstPatch = typeGroup.First();
                    var targetType = FindTypeWithStateMachineSupport(typeGroup.Key, firstPatch.TargetMethodName, firstPatch.TargetAssembly);
                    if (targetType == null)
                    {
                        Log.Warning($"[UTF] Could not find target type: {typeGroup.Key}");
                        failedCount += typeGroup.Count();
                        continue;
                    }

                    foreach (var patch in typeGroup)
                    {
                        try
                        {
                            if (ApplyTranslationPatchOptimized(patch, targetType))
                            {
                                appliedCount++;
                            }
                            else
                            {
                                failedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failedCount++;
                            Log.Error($"[UTF] Failed to apply translation patch {patch.TargetTypeName}.{patch.TargetMethodName}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failedCount += typeGroup.Count();
                    Log.Error($"[UTF] Failed to process type group {typeGroup.Key}: {ex.Message}");
                }
            }

            Log.Message($"[UTF] Patch application complete: {appliedCount} succeeded, {failedCount} failed");
        }

        /// <summary>
        /// Apply a single translation patch with intelligent state machine detection
        /// </summary>
        private static bool ApplyTranslationPatchOptimized(TranslationPatch patch, Type targetType)
        {
            try
            {
                Type actualTargetType = targetType;
                string actualMethodName = patch.TargetMethodName;
                MethodInfo targetMethod = null;

                // 1. 首先检查是否已经是状态机类型
                if (IsStateMachineType(targetType))
                {
                    // 如果是状态机类型，直接使用MoveNext
                    actualMethodName = "MoveNext";
                    targetMethod = AccessTools.Method(targetType, "MoveNext");
                    Log.Message($"[UTF] Direct state machine type detected: {targetType.FullName}.MoveNext");
                }
                else
                {
                    // 2. 尝试在原始类型上查找方法
                    targetMethod = AccessTools.Method(targetType, patch.TargetMethodName);

                    if (targetMethod != null)
                    {
                        // 3. 方法找到了，但检查是否需要使用状态机
                        if (IsIteratorMethod(targetMethod) || IsAsyncMethod(targetMethod))
                        {
                            Log.Message($"[UTF] Found iterator/async method {targetType.FullName}.{patch.TargetMethodName}, searching for state machine...");

                            // 查找对应的状态机类型
                            var stateMachineType = FindStateMachineType(targetType.FullName, patch.TargetMethodName);
                            if (stateMachineType != null)
                            {
                                actualTargetType = stateMachineType;
                                actualMethodName = "MoveNext";
                                targetMethod = AccessTools.Method(stateMachineType, "MoveNext");

                                if (targetMethod != null)
                                {
                                    Log.Message($"[UTF] ✓ Auto-converted {patch.TargetTypeName}.{patch.TargetMethodName} -> {stateMachineType.FullName}.MoveNext");
                                }
                                else
                                {
                                    Log.Warning($"[UTF] Found state machine type but no MoveNext method: {stateMachineType.FullName}");
                                    return false;
                                }
                            }
                            else
                            {
                                Log.Message($"[UTF] Could not find state machine for iterator/async method, will patch original method");
                                // 保持原始方法，可能是非标准的迭代器实现
                            }
                        }
                        else
                        {
                            // Log.Message($"[UTF] Regular method found: {targetType.FullName}.{patch.TargetMethodName}");
                        }
                    }
                    else
                    {
                        // 4. 原始方法未找到，尝试状态机检测
                        Log.Message($"[UTF] Method {patch.TargetTypeName}.{patch.TargetMethodName} not found, attempting state machine detection...");

                        var stateMachineType = FindStateMachineType(targetType.FullName, patch.TargetMethodName);
                        if (stateMachineType != null)
                        {
                            actualTargetType = stateMachineType;
                            actualMethodName = "MoveNext";
                            targetMethod = AccessTools.Method(stateMachineType, "MoveNext");

                            if (targetMethod != null)
                            {
                                Log.Message($"[UTF] ✓ Auto-discovered state machine: {stateMachineType.FullName}.MoveNext");
                            }
                        }
                    }
                }

                if (targetMethod == null)
                {
                    Log.Warning($"[UTF] Could not find target method: {actualTargetType.FullName}.{actualMethodName}");
                    return false;
                }

                // Build translation map using regular Dictionary (compatible approach)
                var translationMap = new Dictionary<string, string>();
                var formatTranslations = new List<StringTranslation>();
                
                foreach (var translation in patch.Translations)
                {
                    // For exact match translations
                    if (!translation.IsFormatString && !translation.IsRegex)
                    {
                        translationMap[translation.OriginalText] = translation.TranslatedText;
                    }
                    else
                    {
                        // For format strings and regex patterns
                        formatTranslations.Add(translation);
                    }
                }

                // Cache translation map and format patterns
                var methodId = $"{actualTargetType.FullName}.{actualMethodName}";
                TranslationCache.RegisterTranslations(methodId, translationMap);
                TranslationCache.RegisterFormatPatterns(methodId, formatTranslations);

                // Apply Harmony patch
                var transpilerMethod = typeof(UniversalStringTranspiler).GetMethod(nameof(UniversalStringTranspiler.ReplaceStrings));
                _harmony.Patch(targetMethod, transpiler: new HarmonyMethod(transpilerMethod));

                Log.Message($"[UTF] Translation patch applied: {actualTargetType.FullName}.{actualMethodName} ({patch.Translations.Count} strings)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] Exception in ApplyTranslationPatchOptimized: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Find type by name, optionally from a specific assembly (optimized version with caching)
        /// </summary>
        private static Type FindTypeOptimized(string typeName, string assemblyName = null)
        {
            // Build cache key
            var cacheKey = string.IsNullOrEmpty(assemblyName) ? typeName : $"{assemblyName}:{typeName}";
            
            return _typeCache.GetOrAdd(cacheKey, key =>
            {
                try
                {
                    // If an assembly is specified, look up from that assembly first
                    if (!string.IsNullOrEmpty(assemblyName))
                    {
                        var assembly = _assemblyCache.GetOrAdd(assemblyName, name =>
                        {
                            try
                            {
                                // 尝试多种方式加载程序集
                                Assembly asm = null;
                                
                                // Method 1: Load directly from file
                                if (File.Exists(name))
                                {
                                    asm = Assembly.LoadFrom(name);
                                }
                                
                                // Method 2: Search from already loaded assemblies
                                if (asm == null)
                                {
                                    asm = AppDomain.CurrentDomain.GetAssemblies()
                                        .FirstOrDefault(a => a.GetName().Name == Path.GetFileNameWithoutExtension(name));
                                }
                                
                                // Method 3: Try Load method
                                if (asm == null)
                                {
                                    asm = Assembly.Load(name);
                                }
                                
                                return asm;
                            }
                            catch
                            {
                                return null;
                            }
                        });

                        if (assembly != null)
                        {
                            var type = assembly.GetType(typeName);
                            if (type != null)
                                return type;
                        }
                    }

                    // Fallback to AccessTools search
                    return AccessTools.TypeByName(typeName);
                }
                catch (Exception ex)
                {
                    Log.Error($"[UTF] Error finding type {typeName}: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Automatically discover state machine types (such as those generated by async/await or yield return)
        /// </summary>
        private static Type FindStateMachineType(string baseTypeName, string methodName)
        {
            try
            {
                // 首先尝试找到基础类型
                var baseType = FindTypeOptimized(baseTypeName);
                if (baseType == null)
                {
                    Log.Warning($"[UTF] Could not find base type: {baseTypeName}");
                    return null;
                }

                // Get all nested types
                var nestedTypes = baseType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);
                
                // Search for common state machine naming patterns
                var stateMachinePatterns = new[]
                {
                    $"<{methodName}>d__",     // 标准编译器生成的状态机模式
                    $"<{methodName}>c__",     // 某些情况下的编译器生成模式
                    $"{methodName}StateMachine", // 自定义状态机命名
                    $"{methodName}Enumerator"    // 枚举器模式
                };

                foreach (var nestedType in nestedTypes)
                {
                    var typeName = nestedType.Name;
                    
                    // 检查是否匹配状态机模式
                    foreach (var pattern in stateMachinePatterns)
                    {
                        if (typeName.StartsWith(pattern))
                        {
                            // Log.Message($"[UTF] Found state machine type: {nestedType.FullName}");
                            return nestedType;
                        }
                    }
                }

                // 如果没有找到明确的状态机，尝试查找实现了IEnumerator或IAsyncStateMachine的嵌套类型
                foreach (var nestedType in nestedTypes)
                {
                    var interfaces = nestedType.GetInterfaces();
                    if (interfaces.Any(i => i.Name == "IEnumerator" || i.Name == "IAsyncStateMachine"))
                    {
                        Log.Message($"[UTF] Found state machine type by interface: {nestedType.FullName}");
                        return nestedType;
                    }
                }

                Log.Warning($"[UTF] Could not find state machine type for {baseTypeName}.{methodName}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] Error finding state machine type for {baseTypeName}.{methodName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Enhanced type lookup method, supports automatic state machine detection
        /// </summary>
        private static Type FindTypeWithStateMachineSupport(string typeName, string methodName, string assemblyName = null)
        {
            // First try direct type lookup
            var directType = FindTypeOptimized(typeName, assemblyName);
            if (directType != null)
            {
                return directType;
            }

            // If direct lookup fails, check if it is a state machine type format
            if (typeName.Contains("<") && typeName.Contains(">") && typeName.Contains("d__"))
            {
                // Looks like a state machine type, try to parse base type
                var baseTypeName = ExtractBaseTypeFromStateMachine(typeName);
                var extractedMethodName = ExtractMethodNameFromStateMachine(typeName);
                
                if (!string.IsNullOrEmpty(baseTypeName))
                {
                    return FindStateMachineType(baseTypeName, extractedMethodName ?? methodName);
                }
            }

            // If method name is provided, try to find state machine from base type
            if (!string.IsNullOrEmpty(methodName))
            {
                return FindStateMachineType(typeName, methodName);
            }

            return null;
        }

        /// <summary>
        /// Extract base type name from state machine type name
        /// Example: MechCaller.MechConsole+<GetGizmos>d__7 -> MechCaller.MechConsole
        /// </summary>
        private static string ExtractBaseTypeFromStateMachine(string stateMachineTypeName)
        {
            try
            {
                var plusIndex = stateMachineTypeName.LastIndexOf('+');
                if (plusIndex > 0)
                {
                    return stateMachineTypeName.Substring(0, plusIndex);
                }
                
                var lessThanIndex = stateMachineTypeName.IndexOf('<');
                if (lessThanIndex > 0)
                {
                    return stateMachineTypeName.Substring(0, lessThanIndex);
                }
                
                return stateMachineTypeName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract method name from state machine type name
        /// Example: MechCaller.MechConsole+<GetGizmos>d__7 -> GetGizmos
        /// </summary>
        private static string ExtractMethodNameFromStateMachine(string stateMachineTypeName)
        {
            try
            {
                var startIndex = stateMachineTypeName.IndexOf('<');
                var endIndex = stateMachineTypeName.IndexOf('>');
                
                if (startIndex >= 0 && endIndex > startIndex)
                {
                    return stateMachineTypeName.Substring(startIndex + 1, endIndex - startIndex - 1);
                }
                
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Check if type is a state machine type
        /// </summary>
        private static bool IsStateMachineType(Type type)
        {
            if (type == null) return false;
            
            // Check if type name contains state machine features
            var typeName = type.Name;
            if (typeName.Contains("d__") && typeName.Contains("<") && typeName.Contains(">"))
            {
                return true;
            }
            
            // Check if implements related interfaces
            var interfaces = type.GetInterfaces();
            return interfaces.Any(i => i.Name == "IEnumerator" || 
                                     i.Name == "IAsyncStateMachine" ||
                                     i.Name == "IEnumerator`1");
        }

        /// <summary>
        /// Smart state machine detection: check if method is likely an iterator or async method
        /// </summary>
        private static bool IsPotentialStateMachineMethod(Type type, string methodName)
        {
            try
            {
                // 检查方法是否存在
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (method == null)
                    return true; // 如果方法不存在，可能是状态机方法

                // 检查返回类型是否表明这是一个迭代器或异步方法
                var returnType = method.ReturnType;
                
                // Iterator methods usually return IEnumerable<T> or IEnumerator<T>
                if (returnType.IsGenericType)
                {
                    var genericTypeDef = returnType.GetGenericTypeDefinition();
                    if (genericTypeDef == typeof(IEnumerable<>) || 
                        genericTypeDef == typeof(IEnumerator<>) ||
                        returnType.Name.Contains("IEnumerable") ||
                        returnType.Name.Contains("IEnumerator"))
                    {
                        return true;
                    }
                }

                // Async methods usually return Task or Task<T>
                if (returnType == typeof(System.Threading.Tasks.Task) || 
                    (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>)))
                {
                    return true;
                }

                // 检查方法是否有 async 关键字（通过IL检查）
                // 这个比较复杂，暂时跳过

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查方法是否是迭代器方法（使用 yield return）
        /// </summary>
        private static bool IsIteratorMethod(MethodInfo method)
        {
            try
            {
                // Check if return type is IEnumerable or IEnumerator
                var returnType = method.ReturnType;
                if (returnType == typeof(System.Collections.IEnumerable) ||
                    returnType == typeof(System.Collections.IEnumerator) ||
                    (returnType.IsGenericType && 
                     (returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
                      returnType.GetGenericTypeDefinition() == typeof(IEnumerator<>))))
                {
                    // Further check: see if there is a corresponding state machine nested type
                    var declaringType = method.DeclaringType;
                    if (declaringType != null)
                    {
                        var nestedTypes = declaringType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);
                        var expectedStateMachineName = $"<{method.Name}>";
                        
                        foreach (var nestedType in nestedTypes)
                        {
                            if (nestedType.Name.Contains(expectedStateMachineName) && 
                                nestedType.Name.Contains("d__"))
                            {
                                return true;
                            }
                        }
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }        /// <summary>
        /// 检查方法是否是异步方法（使用 async/await）
        /// </summary>
        private static bool IsAsyncMethod(MethodInfo method)
        {
            try
            {
                // Check if return type is Task or Task<T>
                var returnType = method.ReturnType;
                if (returnType == typeof(System.Threading.Tasks.Task) ||
                    (returnType.IsGenericType && 
                     returnType.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>)))
                {
                    // Further check: see if there is a corresponding state machine nested type
                    var declaringType = method.DeclaringType;
                    if (declaringType != null)
                    {
                        var nestedTypes = declaringType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);
                        var expectedStateMachineName = $"<{method.Name}>";
                        
                        foreach (var nestedType in nestedTypes)
                        {
                            if (nestedType.Name.Contains(expectedStateMachineName) && 
                                (nestedType.Name.Contains("d__") || nestedType.Name.Contains("c__")))
                            {
                                return true;
                            }
                        }
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get initialization status
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// Get loaded patches count
        /// </summary>
        public static int LoadedPatchesCount => _initialized ? _patches.Value.Count : 0;

        /// <summary>
        /// Force initialization (for testing purposes)
        /// </summary>
        public static void ForceInitialize()
        {
            if (!_initialized)
            {
                InitializeFramework();
            }
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public static string GetCacheStatistics()
        {
            return $"Type cache: {_typeCache.Count} entries, Assembly cache: {_assemblyCache.Count} entries";
        }

        /// <summary>
        /// Smart patch conversion: automatically convert normal method patches to state machine patches
        /// </summary>
        public static void ConvertToStateMachinePatch(string originalXmlPath, string outputXmlPath = null)
        {
            try
            {
                if (!File.Exists(originalXmlPath))
                {
                    Log.Error($"[UTF] Original XML file not found: {originalXmlPath}");
                    return;
                }

                var doc = XDocument.Load(originalXmlPath);
                var root = doc.Root;

                if (root?.Name != "Patch")
                {
                    Log.Error($"[UTF] Invalid patch file format: Root element must be <Patch>");
                    return;
                }

                var operations = root.Elements("Operation")
                    .Where(op => op.Attribute("Class")?.Value == "UniversalTranslationFramework.PatchOperationStringTranslate")
                    .ToList();

                var hasConversions = false;

                foreach (var operation in operations)
                {
                    var targetTypeElement = operation.Element("targetType");
                    var targetMethodElement = operation.Element("targetMethod");

                    if (targetTypeElement == null || targetMethodElement == null)
                        continue;

                    var targetTypeName = targetTypeElement.Value;
                    var targetMethodName = targetMethodElement.Value;

                    // Try to find state machine type
                    var stateMachineType = FindStateMachineType(targetTypeName, targetMethodName);
                    if (stateMachineType != null)
                    {
                        // Update XML to use state machine type
                        targetTypeElement.Value = stateMachineType.FullName;
                        targetMethodElement.Value = "MoveNext"; // State machines usually use MoveNext method

                        hasConversions = true;
                        Log.Message($"[UTF] Converted patch: {targetTypeName}.{targetMethodName} -> {stateMachineType.FullName}.MoveNext");
                    }
                }

                if (hasConversions)
                {
                    var outputPath = outputXmlPath ?? Path.ChangeExtension(originalXmlPath, ".StateMachine.xml");
                    doc.Save(outputPath);
                    Log.Message($"[UTF] Converted patch file saved to: {outputPath}");
                }
                else
                {
                    Log.Message($"[UTF] No state machine conversions needed for: {originalXmlPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] Error converting patch file: {ex.Message}");
            }
        }

        /// <summary>
        /// Batch convert all XML files in the Patches directory
        /// </summary>
        public static void ConvertAllPatchesToStateMachine(string patchesDirectory)
        {
            try
            {
                if (!Directory.Exists(patchesDirectory))
                {
                    Log.Error($"[UTF] Patches directory not found: {patchesDirectory}");
                    return;
                }

                var xmlFiles = Directory.GetFiles(patchesDirectory, "*StringTranslation*.xml", SearchOption.AllDirectories);
                var conversionCount = 0;

                foreach (var xmlFile in xmlFiles)
                {
                    try
                    {
                        var outputPath = Path.Combine(Path.GetDirectoryName(xmlFile), 
                            Path.GetFileNameWithoutExtension(xmlFile) + ".StateMachine.xml");
                        
                        ConvertToStateMachinePatch(xmlFile, outputPath);
                        conversionCount++;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[UTF] Failed to convert {xmlFile}: {ex.Message}");
                    }
                }

                Log.Message($"[UTF] Batch conversion completed: {conversionCount} files processed");
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF] Error in batch conversion: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate XML template for state machine patch
        /// </summary>
        public static string GenerateStateMachinePatchTemplate(string baseTypeName, string methodName, 
            Dictionary<string, string> translations)
        {
            try
            {
                var stateMachineType = FindStateMachineType(baseTypeName, methodName);
                if (stateMachineType == null)
                {
                    return $"<!-- Could not find state machine type for {baseTypeName}.{methodName} -->";
                }

                var xml = new XDocument(
                    new XElement("Patch",
                        new XElement("Operation",
                            new XAttribute("Class", "UniversalTranslationFramework.PatchOperationStringTranslate"),
                            new XElement("targetType", stateMachineType.FullName),
                            new XElement("targetMethod", "MoveNext"),
                            new XElement("replacements",
                                translations.Select(kvp =>
                                    new XElement("li",
                                        new XElement("find", kvp.Key),
                                        new XElement("replace", kvp.Value)
                                    )
                                )
                            )
                        )
                    )
                );

                return xml.ToString();
            }
            catch (Exception ex)
            {
                return $"<!-- Error generating template: {ex.Message} -->";
            }
        }

        /// <summary>
        /// Public method: find state machine type (for debugging)
        /// </summary>
        public static Type FindStateMachineTypePublic(string baseTypeName, string methodName)
        {
            return FindStateMachineType(baseTypeName, methodName);
        }
    }
}