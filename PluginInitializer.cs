using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Autodesk.AutoCAD.Runtime;

// AutoCAD вызовет Initialize() при NETLOAD — до первого обращения к OR-Tools
[assembly: ExtensionApplication(typeof(InsulationMasterPro.PluginInitializer))]

namespace InsulationMasterPro
{
    /// <summary>
    /// Инициализатор плагина: регистрирует резолвер нативных DLL,
    /// чтобы google-ortools-native.dll загружалась из папки плагина.
    /// </summary>
    public class PluginInitializer : IExtensionApplication
    {
        private static bool _resolverRegistered;

        public void Initialize()
        {
            RegisterManagedResolver();
            PreloadExcelAssemblies();
            RegisterNativeResolver();
        }

        public void Terminate()
        {
        }

        /// <summary>
        /// Предзагружает ClosedXML и все его зависимости в Default ALC,
        /// чтобы они были в памяти ДО первого обращения к Excel-коду.
        /// 
        /// Используем AssemblyLoadContext.Default.LoadFromAssemblyPath — это правильно
        /// регистрирует сборку по имени (identity) в Default ALC. Когда JIT запросит
        /// ClosedXML, он найдёт её уже загруженной БЕЗ вызова LoadFromResolveHandler.
        /// 
        /// Assembly.Load(byte[]) не подходит — создаёт анонимную сборку без identity,
        /// и LoadFromResolveHandler потом падает с 0x80131621 при попытке повторной загрузки.
        /// </summary>
        private static void PreloadExcelAssemblies()
        {
            string pluginDir = Path.GetDirectoryName(
                typeof(PluginInitializer).Assembly.Location) ?? "";

            string[] excelDeps =
            {
                "System.IO.Packaging.dll",
                "ExcelNumberFormat.dll",
                "RBush.dll",
                "SixLabors.Fonts.dll",
                "DocumentFormat.OpenXml.Framework.dll",
                "DocumentFormat.OpenXml.dll",
                "ClosedXML.Parser.dll",
                "ClosedXML.dll"
            };

            foreach (var dll in excelDeps)
            {
                string path = Path.Combine(pluginDir, dll);
                if (!File.Exists(path)) continue;

                try
                {
                    var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                    string simpleName = asm.GetName().Name ?? dll;
                    _assemblyCache.TryAdd(simpleName, asm);
                    LogResolve($"[PRELOAD OK] {dll} (ALC.Default)");
                }
                catch (System.Exception ex1)
                {
                    LogResolve($"[PRELOAD ALC FAIL] {dll}: {ex1.Message}");
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        var asm = Assembly.Load(bytes);
                        string simpleName = asm.GetName().Name ?? dll;
                        _assemblyCache.TryAdd(simpleName, asm);
                        LogResolve($"[PRELOAD OK] {dll} (byte[] fallback)");
                    }
                    catch (System.Exception ex2)
                    {
                        LogResolve($"[PRELOAD FAIL] {dll}: {ex2.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Регистрирует AppDomain.AssemblyResolve для managed DLL (ClosedXML, DocumentFormat.OpenXml, и т.д.),
        /// которые AutoCAD не находит в стандартных путях загрузки.
        /// Без этого Excel-отчёт не создаётся — ClosedXML.dll не загружается.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Assembly>
            _assemblyCache = new();

        private static void RegisterManagedResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    // Извлекаем простое имя сборки (без версии, culture, publicKey)
                    var assemblyName = new AssemblyName(args.Name);
                    string simpleName = assemblyName.Name ?? "";

                    // Проверяем кэш — уже загружена?
                    if (_assemblyCache.TryGetValue(simpleName, out var cached))
                        return cached;

                    string pluginDir = Path.GetDirectoryName(
                        typeof(PluginInitializer).Assembly.Location) ?? "";
                    string dllPath = Path.Combine(pluginDir, simpleName + ".dll");

                    if (!File.Exists(dllPath))
                        return null;

                    LogResolve($"Загрузка {simpleName} из {dllPath}");

                    Assembly? loaded = null;

                    // Стратегия 1: Assembly.LoadFrom (стандартный путь)
                    try
                    {
                        loaded = Assembly.LoadFrom(dllPath);
                        LogResolve($"[OK] {simpleName} загружен через LoadFrom");
                    }
                    catch (System.Exception ex1)
                    {
                        LogResolve($"[WARN] LoadFrom не удался для {simpleName}: {ex1.Message}");

                        // Стратегия 2: Assembly.LoadFile (без контекста привязки)
                        try
                        {
                            loaded = Assembly.LoadFile(dllPath);
                            LogResolve($"[OK] {simpleName} загружен через LoadFile");
                        }
                        catch (System.Exception ex2)
                        {
                            LogResolve($"[WARN] LoadFile не удался для {simpleName}: {ex2.Message}");

                            // Стратегия 3: Assembly.Load(byte[]) — обходит ВСЕ проверки контекста
                            try
                            {
                                byte[] assemblyBytes = File.ReadAllBytes(dllPath);
                                loaded = Assembly.Load(assemblyBytes);
                                LogResolve($"[OK] {simpleName} загружен через Load(byte[])");
                            }
                            catch (System.Exception ex3)
                            {
                                LogResolve($"[FAIL] Все стратегии загрузки {simpleName} не удались: {ex3.Message}");
                            }
                        }
                    }

                    if (loaded != null)
                    {
                        _assemblyCache.TryAdd(simpleName, loaded);
                    }

                    return loaded;
                }
                catch (System.Exception ex)
                {
                    LogResolve($"[FAIL] Критическая ошибка резолва {args.Name}: {ex}");
                }
                return null;
            };
        }

        /// <summary>
        /// Логирует операции резолвера в database.log (персистентный лог).
        /// </summary>
        private static void LogResolve(string message)
        {
            try
            {
                string logFile = @"C:\DB_INS\database.log";
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [AssemblyResolve] {message}";
                File.AppendAllText(logFile, logEntry + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>
        /// Регистрирует NativeLibrary.SetDllImportResolver для сборки Google.OrTools,
        /// чтобы P/Invoke нашёл google-ortools-native.dll рядом с плагином.
        /// </summary>
        internal static void RegisterNativeResolver()
        {
            if (_resolverRegistered) return;
            _resolverRegistered = true;

            try
            {
                // Определяем папку, из которой загружен плагин
                string pluginDir = Path.GetDirectoryName(
                    typeof(PluginInitializer).Assembly.Location) ?? "";

                // Пре-загружаем нативную DLL, чтобы она была доступна до любого P/Invoke
                string nativePath = Path.Combine(pluginDir, "google-ortools-native.dll");
                if (File.Exists(nativePath))
                {
                    NativeLibrary.Load(nativePath);
                    return; // Достаточно одной пре-загрузки
                }

                // Fallback: ищем в runtimes/win-x64/native/
                string runtimePath = Path.Combine(pluginDir, "runtimes", "win-x64", "native",
                    "google-ortools-native.dll");
                if (File.Exists(runtimePath))
                {
                    NativeLibrary.Load(runtimePath);
                    return;
                }

                // Последний вариант: SetDllImportResolver для ленивой загрузки
                var orToolsAssembly = typeof(Google.OrTools.Sat.CpSolver).Assembly;
                NativeLibrary.SetDllImportResolver(orToolsAssembly, (libraryName, assembly, searchPath) =>
                {
                    string dir = Path.GetDirectoryName(assembly.Location)
                                 ?? Path.GetDirectoryName(typeof(PluginInitializer).Assembly.Location)
                                 ?? "";

                    // Ищем рядом с плагином
                    string candidate = Path.Combine(dir, libraryName);
                    if (!candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        candidate += ".dll";

                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                        return handle;

                    // Ищем в runtimes/win-x64/native/
                    string rtCandidate = Path.Combine(dir, "runtimes", "win-x64", "native",
                        Path.GetFileName(candidate));
                    if (File.Exists(rtCandidate) && NativeLibrary.TryLoad(rtCandidate, out handle))
                        return handle;

                    return IntPtr.Zero; // fallback на стандартный поиск
                });
            }
            catch
            {
                // Не блокируем загрузку плагина, если резолвер не удалось зарегистрировать
            }
        }
    }
}
