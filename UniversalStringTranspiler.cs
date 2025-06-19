using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace UniversalTranslationFramework
{
    /// <summary>
    /// Universal string replacement transpiler (optimized version)
    /// This class contains Harmony transpiler methods that replace string constants at runtime.
    /// </summary>
    [HarmonyPatch]
    public static class UniversalStringTranspiler
    {
        /// <summary>
        /// Harmony transpiler method: replaces string constants in the target method (optimized version)
        /// </summary>
        /// <param name="instructions">Original IL instruction sequence</param>
        /// <param name="original">Original method being patched</param>
        /// <returns>Modified IL instruction sequence</returns>
        public static IEnumerable<CodeInstruction> ReplaceStrings(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            // Construct the method identifier
            var methodId = $"{original.DeclaringType?.FullName}.{original.Name}";
            
            // Get the translation mapping for this method
            var translations = TranslationCache.GetTranslations(methodId);
            
            if (translations == null || translations.Count == 0)
            {
                // No translations found; return the original instructions directly
                return instructions;
            }

            var instructionList = instructions.ToList(); // 转换一次，避免多次枚举
            var replacedCount = 0;
            
            // Iterate over all IL instructions
            for (int i = 0; i < instructionList.Count; i++)
            {
                var instruction = instructionList[i];
                
                // Check if it's a string load instruction (ldstr)
                if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string originalString)
                {
                    string translatedString = null;
                    
                    // First try exact match (fastest)
                    if (translations.TryGetValue(originalString, out translatedString))
                    {
                        // Replace with the translated string
                        instructionList[i] = new CodeInstruction(OpCodes.Ldstr, translatedString);
                        replacedCount++;
                    }
                    else
                    {
                        // Try format pattern matching
                        translatedString = TranslationCache.TryFormatPatternMatch(methodId, originalString);
                        if (translatedString != null)
                        {
                            instructionList[i] = new CodeInstruction(OpCodes.Ldstr, translatedString);
                            replacedCount++;
                        }
                    }
                }
            }
            
            if (replacedCount > 0)
            {
                // Log.Message($"[UTF] Replaced {replacedCount} strings in {methodId}");
            }
            
            return instructionList;
        }

        /// <summary>
        /// Debug transpiler method: logs all string constants without replacing them.
        /// </summary>
        /// <param name="instructions">Original IL instruction sequence</param>
        /// <param name="original">Original method being patched</param>
        /// <returns>Unmodified IL instruction sequence</returns>
        public static IEnumerable<CodeInstruction> DebugLogStrings(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var methodId = $"{original.DeclaringType?.FullName}.{original.Name}";
            var stringConstants = new List<string>();
            var instructionList = instructions.ToList();
            
            foreach (var instruction in instructionList)
            {
                if (instruction.opcode == OpCodes.Ldstr && instruction.operand is string str)
                {
                    stringConstants.Add(str);
                }
            }
            
            if (stringConstants.Count > 0)
            {
                Log.Message($"[UTF DEBUG] {methodId} contains {stringConstants.Count} string constants:");
                for (int i = 0; i < stringConstants.Count; i++)
                {
                    Log.Message($"  [{i}] \"{stringConstants[i]}\"");
                }
            }
            
            return instructionList;
        }

        /// <summary>
        /// Performance monitoring transpiler for development
        /// </summary>
        public static IEnumerable<CodeInstruction> MonitorPerformance(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = ReplaceStrings(instructions, original);
            stopwatch.Stop();
            
            if (stopwatch.ElapsedMilliseconds > 10) // Log if transpilation takes more than 10ms
            {
                Log.Warning($"[UTF PERF] Slow transpilation for {original.DeclaringType?.FullName}.{original.Name}: {stopwatch.ElapsedMilliseconds}ms");
            }
            
            return result;
        }
    }

    /// <summary>
    /// Utility tools for development and debugging (enhanced version)
    /// </summary>
    public static class TranslationDebugTools
    {
        private static readonly Dictionary<string, System.Diagnostics.Stopwatch> _methodTimers = 
            new Dictionary<string, System.Diagnostics.Stopwatch>();

        /// <summary>
        /// Apply debug transpiler to the specified method to log string constants it contains.
        /// </summary>
        /// <param name="targetType">Target type</param>
        /// <param name="methodName">Target method name</param>
        public static void DebugMethod(Type targetType, string methodName)
        {
            try
            {
                var method = AccessTools.Method(targetType, methodName);
                if (method == null)
                {
                    Log.Error($"[UTF DEBUG] Cannot find method: {targetType.FullName}.{methodName}");
                    return;
                }

                var harmony = new Harmony("UTF.Debug.StringInspector");
                var transpilerMethod = typeof(UniversalStringTranspiler).GetMethod(nameof(UniversalStringTranspiler.DebugLogStrings));
                harmony.Patch(method, transpiler: new HarmonyMethod(transpilerMethod));
                
                Log.Message($"[UTF DEBUG] Debug patch applied to {targetType.FullName}.{methodName}");
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF DEBUG] Error while debugging method: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply performance monitoring to a method
        /// </summary>
        public static void MonitorMethodPerformance(Type targetType, string methodName)
        {
            try
            {
                var method = AccessTools.Method(targetType, methodName);
                if (method == null)
                {
                    Log.Error($"[UTF DEBUG] Cannot find method: {targetType.FullName}.{methodName}");
                    return;
                }

                var harmony = new Harmony("UTF.Debug.PerformanceMonitor");
                var transpilerMethod = typeof(UniversalStringTranspiler).GetMethod(nameof(UniversalStringTranspiler.MonitorPerformance));
                harmony.Patch(method, transpiler: new HarmonyMethod(transpilerMethod));
                
                Log.Message($"[UTF DEBUG] Performance monitoring applied to {targetType.FullName}.{methodName}");
            }
            catch (Exception ex)
            {
                Log.Error($"[UTF DEBUG] Error while applying performance monitoring: {ex.Message}");
            }
        }

        /// <summary>
        /// Print translation cache statistics.
        /// </summary>
        public static void PrintCacheStats()
        {
            Log.Message($"[UTF DEBUG] Translation cache stats: {TranslationCache.GetCacheStats()}");
        }

        /// <summary>
        /// Print detailed framework statistics
        /// </summary>
        public static void PrintFrameworkStats()
        {
            Log.Message($"[UTF DEBUG] Framework Status:");
            Log.Message($"  Initialized: {TranslationFrameworkMod.IsInitialized}");
            Log.Message($"  Loaded Patches: {TranslationFrameworkMod.LoadedPatchesCount}");
            Log.Message($"  {TranslationCache.GetCacheStats()}");
            
            // Memory usage information
            var memoryBefore = GC.GetTotalMemory(false);
            GC.Collect();
            var memoryAfter = GC.GetTotalMemory(true);
            Log.Message($"  Memory Usage: {memoryAfter / 1024 / 1024}MB (freed {(memoryBefore - memoryAfter) / 1024}KB)");
        }

        /// <summary>
        /// Benchmark translation performance
        /// </summary>
        public static void BenchmarkTranslations()
        {
            const int iterations = 1000;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            for (int i = 0; i < iterations; i++)
            {
                TranslationCache.GetCacheStats();
            }
            
            stopwatch.Stop();
            Log.Message($"[UTF DEBUG] Benchmark: {iterations} cache operations in {stopwatch.ElapsedMilliseconds}ms " +
                       $"({(double)stopwatch.ElapsedMilliseconds / iterations:F3}ms per operation)");
        }
    }
}