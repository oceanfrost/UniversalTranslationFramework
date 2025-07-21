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

        private static readonly HashSet<string> SupportedOperationClasses = new HashSet<string>
        {
            "UniversalTranslationFramework.PatchOperationStringTranslate",
            "PatchOperationStringTranslate"
        };

        static TranslationFrameworkMod()
        {
            // Use LongEventHandler for asynchronous initialization to avoid blocking game startup
            LongEventHandler.QueueLongEvent(() => InitializeFramework(), null,
                false, null);
        }

        private static void InitializeFramework()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                try
                {
                    // Log.Message("[UTF] Universal Translation Framework is starting...");

                    _harmony = new Harmony(FRAMEWORK_HARMONY_ID);

                    // Trigger lazy loading
                    var patches = _patches.Value;
                    ApplyAllPatches(patches);

                    // Log.Message($"[UTF] Universal Translation Framework started successfully! Loaded {patches.Count} translation patches.");
                    _initialized = true;
                }
                catch (Exception ex)
                {
                    // Log.Error($"[UTF] Failed to start Universal Translation Framework: {ex}");
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
                // Log.Error($"[UTF] Error during parallel processing, falling back to sequential: {ex.Message}");

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
                        // Log.Error($"[UTF] Error scanning mod {mod.Name}: {modEx.Message}");
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

                var translationFiles = Directory.EnumerateFiles(patchesDir, "*Translation*.xml",
                    SearchOption.AllDirectories);

                foreach (var filePath in translationFiles)
                {
                    try
                    {
                        var filePatches = LoadTranslationPatchesFromFileOptimized(filePath, mod);
                        patches.AddRange(filePatches);

                        // Log.Message($"[UTF] Loaded {filePatches.Count} translation patches from {Path.GetFileName(filePath)}");
                    }
                    catch (Exception ex)
                    {
                        // Log.Error($"[UTF] Failed to load translation patch file {filePath}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Error occurred while scanning mod '{mod.Name}': {ex.Message}");
            }

            return patches;
        }

        /// <summary>
        /// Load translation patches from an XML file (optimized version with empty file handling)
        /// </summary>
        private static List<TranslationPatch> LoadTranslationPatchesFromFileOptimized(string filePath,
            ModContentPack mod)
        {
            var patches = new List<TranslationPatch>();

            try
            {
                // Use safer XML loading method
                XDocument doc;
                try
                {
                    using (var fileStream =
                           new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192))
                    using (var reader = XmlReader.Create(fileStream, new XmlReaderSettings
                    {
                        IgnoreWhitespace = true,
                        IgnoreComments = true,
                        IgnoreProcessingInstructions = true,
                        DtdProcessing = DtdProcessing.Ignore,
                        CheckCharacters = false,
                        ConformanceLevel = ConformanceLevel.Auto
                    }))
                    {
                        doc = XDocument.Load(reader);
                    }
                }
                catch (XmlException xmlEx)
                {
                    // Log.Error($"[UTF] XML parsing failed for {filePath}: {xmlEx.Message}");
                    return patches;
                }
                catch (Exception)
                {
                    // Fallback to simple loading method
                    try
                    {
                        doc = XDocument.Load(filePath);
                    }
                    catch (Exception fallbackEx)
                    {
                        // Log.Error($"[UTF] Fallback XML loading also failed for {filePath}: {fallbackEx.Message}");
                        return patches;
                    }
                }

                var root = doc.Root;

                if (root == null)
                {
                    // Log.Warning($"[UTF] XML file has no root element: {filePath}");
                    return patches;
                }

                if (root.Name != "Patch")
                {
                    // Log.Warning($"[UTF] Invalid patch file format {filePath}: Root element must be <Patch>, found: {root.Name}");
                    return patches;
                }

                if (!root.HasElements)
                {
                    // Log.Warning($"[UTF] Empty patch file (no operations): {filePath}");
                    return patches;
                }

                var operations = root.Elements("Operation")
                    .Where(op =>
                    {
                        var classAttr = op.Attribute("Class")?.Value;
                        return !string.IsNullOrEmpty(classAttr) && SupportedOperationClasses.Contains(classAttr);
                    });

                var operationsList = operations.ToList();
                if (operationsList.Count == 0)
                {
                    // Log.Warning($"[UTF] No valid translation operations found in {filePath}");
                    return patches;
                }

                foreach (var operation in operationsList)
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
                // Log.Error($"[UTF] Unexpected error processing file {filePath}: {ex}");
            }

            return patches;
        }

        /// <summary>
        /// Parse a single string translation patch operation (optimized version)
        /// </summary>
        private static TranslationPatch ParseStringTranslationPatchOptimized(XElement operation, ModContentPack mod,
            string sourceFile)
        {
            try
            {
                var targetAssembly = operation.Element("targetAssembly")?.Value;
                var targetType = operation.Element("targetType")?.Value;
                var targetMethod = operation.Element("targetMethod")?.Value;

                if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(targetMethod))
                {
                    // Log.Warning($"[UTF] Skipping invalid patch operation: Missing targetType or targetMethod ({sourceFile})");
                    return null;
                }

                var replacementsElement = operation.Element("replacements");
                if (replacementsElement == null)
                {
                    // Log.Warning($"[UTF] Skipping patch operation without replacements element ({sourceFile})");
                    return null;
                }

                var items = replacementsElement.Elements("li").ToList();
                if (items.Count == 0)
                {
                    // Log.Warning($"[UTF] Skipping patch operation with empty replacements ({sourceFile})");
                    return null;
                }

                var replacements = new List<StringTranslation>(items.Count);

                foreach (var item in items)
                {
                    var original = item.Element("find")?.Value;
                    var translated = item.Element("replace")?.Value;

                    if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(translated))
                    {
                        // Log.Warning($"[UTF] Skipping invalid translation item (empty find or replace) in {sourceFile}");
                        continue;
                    }

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
                            // Log.Warning($"[UTF] Format string placeholder mismatch in {sourceFile}: '{original}' -> '{translated}'");
                        }
                    }

                    // Set context if provided
                    translation.Context = item.Attribute("context")?.Value;

                    replacements.Add(translation);
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
                else
                {
                    // Log.Warning($"[UTF] No valid translations found in patch operation ({sourceFile})");
                }
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Failed to parse translation patch operation ({sourceFile}): {ex.Message}");
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
            var patchesByType = patches.GroupBy(p => p.TargetTypeName).ToList();

            foreach (var typeGroup in patchesByType)
            {
                try
                {
                    // Use enhanced type lookup, support for state machine types
                    var firstPatch = typeGroup.First();
                    var targetType = FindTypeWithStateMachineSupport(typeGroup.Key, firstPatch.TargetMethodName,
                        firstPatch.TargetAssembly);
                    if (targetType == null)
                    {
                        // Log.Warning($"[UTF] Could not find target type: {typeGroup.Key}");
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
                            // Log.Error($"[UTF] Failed to apply translation patch {patch.TargetTypeName}.{patch.TargetMethodName}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failedCount += typeGroup.Count();
                    // Log.Error($"[UTF] Failed to process type group {typeGroup.Key}: {ex.Message}");
                }
            }

            // Log.Message($"[UTF] Patch application complete: {appliedCount} succeeded, {failedCount} failed");
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

                if (IsStateMachineType(targetType))
                {
                    actualMethodName = "MoveNext";
                    targetMethod = AccessTools.Method(targetType, "MoveNext");
                    // Log.Message($"[UTF] Direct state machine type detected: {targetType.FullName}.MoveNext");
                }
                else
                {
                    targetMethod = AccessTools.Method(targetType, patch.TargetMethodName);

                    if (targetMethod != null)
                    {
                        if (IsIteratorMethod(targetMethod) || IsAsyncMethod(targetMethod))
                        {
                            // Log.Message($"[UTF] Found iterator/async method {targetType.FullName}.{patch.TargetMethodName}, searching for state machine...");

                            var stateMachineType = FindStateMachineType(targetType.FullName, patch.TargetMethodName);
                            if (stateMachineType != null)
                            {
                                actualTargetType = stateMachineType;
                                actualMethodName = "MoveNext";
                                targetMethod = AccessTools.Method(stateMachineType, "MoveNext");

                                if (targetMethod != null)
                                {
                                    // Log.Message($"[UTF] ✓ Auto-converted {patch.TargetTypeName}.{patch.TargetMethodName} -> {stateMachineType.FullName}.MoveNext");
                                }
                                else
                                {
                                    // Log.Warning($"[UTF] Found state machine type but no MoveNext method: {stateMachineType.FullName}");
                                    return false;
                                }
                            }
                            else
                            {
                                // Log.Message($"[UTF] Could not find state machine for iterator/async method, will patch original method");
                            }
                        }
                        else
                        {
                            // Log.Message($"[UTF] Regular method found: {targetType.FullName}.{patch.TargetMethodName}");
                        }
                    }
                    else
                    {
                        // Log.Message($"[UTF] Method {patch.TargetTypeName}.{patch.TargetMethodName} not found, attempting state machine detection...");

                        var stateMachineType = FindStateMachineType(targetType.FullName, patch.TargetMethodName);
                        if (stateMachineType != null)
                        {
                            actualTargetType = stateMachineType;
                            actualMethodName = "MoveNext";
                            targetMethod = AccessTools.Method(stateMachineType, "MoveNext");

                            if (targetMethod != null)
                            {
                                // Log.Message($"[UTF] ✓ Auto-discovered state machine: {stateMachineType.FullName}.MoveNext");
                            }
                        }
                    }
                }

                if (targetMethod == null)
                {
                    // Log.Warning($"[UTF] Could not find target method: {actualTargetType.FullName}.{actualMethodName}");
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
                var transpilerMethod =
                    typeof(UniversalStringTranspiler).GetMethod(nameof(UniversalStringTranspiler.ReplaceStrings));
                _harmony.Patch(targetMethod, transpiler: new HarmonyMethod(transpilerMethod));

                // Log.Message($"[UTF] Translation patch applied: {actualTargetType.FullName}.{actualMethodName} ({patch.Translations.Count} strings)");
                return true;
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Exception in ApplyTranslationPatchOptimized: {ex}");
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
                                Assembly asm = null;

                                if (File.Exists(name))
                                {
                                    asm = Assembly.LoadFrom(name);
                                }

                                if (asm == null)
                                {
                                    asm = AppDomain.CurrentDomain.GetAssemblies()
                                        .FirstOrDefault(a =>
                                            a.GetName().Name == Path.GetFileNameWithoutExtension(name));
                                }

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
                    // Log.Error($"[UTF] Error finding type {typeName}: {ex.Message}");
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
                var baseType = FindTypeOptimized(baseTypeName);
                if (baseType == null)
                {
                    // Log.Warning($"[UTF] Could not find base type: {baseTypeName}");
                    return null;
                }

                // Get all nested types
                var nestedTypes = baseType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);

                // Search for common state machine naming patterns
                var stateMachinePatterns = new[]
                {
                    $"<{methodName}>d__",
                    $"<{methodName}>c__",
                    $"{methodName}StateMachine",
                    $"{methodName}Enumerator"
                };

                foreach (var nestedType in nestedTypes)
                {
                    var typeName = nestedType.Name;

                    foreach (var pattern in stateMachinePatterns)
                    {
                        if (typeName.StartsWith(pattern))
                        {
                            // Log.Message($"[UTF] Found state machine type: {nestedType.FullName}");
                            return nestedType;
                        }
                    }
                }

                foreach (var nestedType in nestedTypes)
                {
                    var interfaces = nestedType.GetInterfaces();
                    if (interfaces.Any(i => i.Name == "IEnumerator" || i.Name == "IAsyncStateMachine"))
                    {
                        // Log.Message($"[UTF] Found state machine type by interface: {nestedType.FullName}");
                        return nestedType;
                    }
                }

                // Log.Warning($"[UTF] Could not find state machine type for {baseTypeName}.{methodName}");
                return null;
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Error finding state machine type for {baseTypeName}.{methodName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Enhanced type lookup method, supports automatic state machine detection
        /// </summary>
        private static Type FindTypeWithStateMachineSupport(string typeName, string methodName,
            string assemblyName = null)
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
                    return FindTypeOptimized(baseTypeName, assemblyName);
                }
            }

            return null;
        }

        /// <summary>
        /// Check if a type is a state machine type
        /// </summary>
        private static bool IsStateMachineType(Type type)
        {
            if (type == null) return false;

            var typeName = type.Name;
            return typeName.Contains("<") && typeName.Contains(">") &&
                   (typeName.Contains("d__") || typeName.Contains("c__"));
        }

        /// <summary>
        /// Check if a method is an iterator method
        /// </summary>
        private static bool IsIteratorMethod(MethodInfo method)
        {
            if (method == null) return false;

            var returnType = method.ReturnType;
            return returnType.IsGenericType &&
                   (returnType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
                    returnType.GetGenericTypeDefinition() == typeof(IEnumerator<>)) ||
                   returnType == typeof(System.Collections.IEnumerable) ||
                   returnType == typeof(System.Collections.IEnumerator);
        }

        /// <summary>
        /// Check if a method is an async method
        /// </summary>
        private static bool IsAsyncMethod(MethodInfo method)
        {
            if (method == null) return false;

            var returnType = method.ReturnType;
            return returnType == typeof(Task) ||
                   (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>));
        }

        /// <summary>
        /// Extract base type name from state machine type name
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
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Error extracting base type from state machine name {stateMachineTypeName}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Extract method name from state machine type name
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
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Error extracting method name from state machine name {stateMachineTypeName}: {ex.Message}");
            }

            return null;
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

        public static void ApplyPatchOperationFromXml(TranslationPatch patch)
        {
            try
            {
                if (!_initialized)
                {
                    // Log.Warning("[UTF] Framework not initialized, queueing patch operation");
                    return;
                }

                var targetType = FindTypeWithStateMachineSupport(patch.TargetTypeName, patch.TargetMethodName,
                    patch.TargetAssembly);
                if (targetType == null)
                {
                    // Log.Warning($"[UTF] Could not find target type for XML patch: {patch.TargetTypeName}");
                    return;
                }

                ApplyTranslationPatchOptimized(patch, targetType);
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Error applying patch operation from XML: {ex.Message}");
            }
        }
    }

    public class PatchOperationStringTranslate : PatchOperation
    {
        public string targetType;
        public string targetMethod;
        public string targetAssembly;
        public List<StringReplacementPair> replacements = new List<StringReplacementPair>();

        protected override bool ApplyWorker(XmlDocument xml)
        {
            try
            {
                RegisterPatchOperation();
                return true;
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Error in PatchOperationStringTranslate: {ex.Message}");
                return false;
            }
        }

        private void RegisterPatchOperation()
        {
            try
            {
                if (string.IsNullOrEmpty(targetType) || string.IsNullOrEmpty(targetMethod))
                {
                    // Log.Warning("[UTF] PatchOperationStringTranslate: targetType or targetMethod is empty");
                    return;
                }

                var translationMap = new Dictionary<string, string>();
                foreach (var replacement in replacements)
                {
                    if (!string.IsNullOrEmpty(replacement.find) && !string.IsNullOrEmpty(replacement.replace))
                    {
                        translationMap[replacement.find] = replacement.replace;
                    }
                }

                if (translationMap.Count == 0)
                {
                    // Log.Warning("[UTF] PatchOperationStringTranslate: No valid replacements found");
                    return;
                }

                var patch = new TranslationPatch
                {
                    TargetTypeName = targetType,
                    TargetMethodName = targetMethod,
                    TargetAssembly = targetAssembly,
                    Translations = translationMap.Select(kvp => new StringTranslation
                    {
                        OriginalText = kvp.Key,
                        TranslatedText = kvp.Value
                    }).ToList()
                };

                TranslationFrameworkMod.ApplyPatchOperationFromXml(patch);
            }
            catch (Exception ex)
            {
                // Log.Error($"[UTF] Error registering patch operation: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 字符串替换对 - 用于XML序列化
    /// </summary>
    public class StringReplacementPair
    {
        public string find;
        public string replace;
    }
}