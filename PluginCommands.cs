using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using ClosedXML.Excel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Colors;
using InsulationMasterPro.Logic;
using InsulationMasterPro.Models;
using InsulationMasterPro.Services;
using InsulationMasterPro.Validation;
using InsulationMasterPro.Visualization;
using InsulationMasterPro.OrTools;
using InsulationMasterPro.Data;

[assembly: CommandClass(typeof(InsulationMasterPro.Commands.PluginCommands))]

namespace InsulationMasterPro.Commands
{
    public class PluginCommands
    {
        /// <summary>
        /// Раскладка утеплителя с OR-Tools глобальной оптимизацией.
        /// Минимизирует суммарную площадь остатков на складе.
        /// </summary>
        [CommandMethod("INS")]
        public void RunLayout()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                ed.WriteMessage("\n=== Insulation Master Pro v3.4.1 (OR-Tools) ===");
                ed.WriteMessage("\nРежим: ГЛОБАЛЬНАЯ ОПТИМИЗАЦИЯ (минимизация площади остатков)");
                ed.WriteMessage("\nНастройка выпуска за контур...");
                UserDialogService.AskFirstRowWithRelease();
                ed.WriteMessage("\nПоиск контуров фасада и окон...");

                // 1. Find Facades
                var facadeIds = FindClosedContourIds(db, ed, "Внешний контур фасада");
                if (!facadeIds.Any())
                {
                    ed.WriteMessage("\nОшибка: Не найдено замкнутых полилиний на слое 'Внешний контур фасада'.");
                    return;
                }
                ed.WriteMessage($"\nНайдено фасадов: {facadeIds.Count}");

                // 2. Find Windows
                var windowIds = FindClosedContourIds(db, ed, "Контуры окон");
                ed.WriteMessage($"\nНайдено окон: {windowIds.Count}");

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var facades = GetPolylinesFromIds(tr, facadeIds);
                    var windows = GetPolylinesFromIds(tr, windowIds);

                    ed.WriteMessage("\nВалидация контуров...");
                    var validationResult = ValidationEngine.ValidateMultipleContours(facades, "Фасад");
                    var windowValidationResult = windows.Count == 0
                        ? new Validation.ValidationResult { IsValid = true }
                        : ValidationEngine.ValidateMultipleContours(windows, "Окно");

                    if (!validationResult.IsValid || !windowValidationResult.IsValid)
                    {
                        ed.WriteMessage("\nОшибки валидации:");
                        foreach (var error in validationResult.Errors.Concat(windowValidationResult.Errors))
                            ed.WriteMessage($"\n  - {error}");
                        return;
                    }

                    foreach (var warning in validationResult.Warnings.Concat(windowValidationResult.Warnings))
                        ed.WriteMessage($"\nПредупреждение: {warning}");

                    // Снимок склада ДО раскладки (для Excel-отчёта)
                    var initialStock = RemnantDatabase.LoadRemnants()
                        .Select(r => new Remnant
                        {
                            Id = r.Id, Width = r.Width, Height = r.Height,
                            Source = r.Source, AddedDate = r.AddedDate,
                            BaseBlockNumber = r.BaseBlockNumber,
                            SuffixCounter = r.SuffixCounter
                        }).ToList();

                    ed.WriteMessage("\n🚀 Запуск OR-Tools оптимизации...");
                    
                    var layoutParams = new LayoutParameters 
                    { 
                        FirstRowWithRelease = UserDialogService.FirstRowWithRelease,
                        UseOrTools = true,
                        OrToolsTimeoutSeconds = 60,
                        // HorizonRows=1: каждый ряд независимо (безопасный старт).
                        // HorizonRows>1 реально используется с 22.00 29.03.26 и при значении 3
                        // даёт ~38 мин на фасад вместо ~2 мин.
                        RollingHorizonRows = 1
                    };

                    // [FIX] Log optimization parameters for debugging
                    LogFix(ed, $"Параметры: UseOrTools={layoutParams.UseOrTools}, Horizon={layoutParams.RollingHorizonRows}, Timeout={layoutParams.OrToolsTimeoutSeconds}s, Фасадов={facadeIds.Count}, Окон={windowIds.Count}");
                    
                    LayoutEngine engine = new LayoutEngine(new TileSpecification(), layoutParams);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    LayoutResult result = engine.Process(facades, windows);
                    sw.Stop();

                    // [FIX] Log solve outcome
                    LogFix(ed, $"Process завершён за {sw.ElapsedMilliseconds}мс: Status={result.SolverStatus}, Mode={result.OptimizationMode}, SolveTime={result.SolveTimeMs}мс, Tiles={result.AllTiles.Count}, Errors={result.Errors.Count}");

                    // Диагностический файл (TXT + JSON) должен формироваться для любого исхода обработки.
                    WriteDiagnosticsSafe(ed, result, initialStock);

                    var blockingErrors = result.Errors
                        .Where(error => !IsNonBlockingError(error))
                        .ToList();
                    var nonBlockingErrors = result.Errors
                        .Where(IsNonBlockingError)
                        .ToList();

                    // [FIX] Log error breakdown
                    if (result.Errors.Any())
                        LogFix(ed, $"Ошибок всего: {result.Errors.Count}, блокирующих: {blockingErrors.Count}, неблокирующих: {nonBlockingErrors.Count}");

                    if (blockingErrors.Any())
                    {
                        ed.WriteMessage("\n❌ Ошибки при обработке (решение OR-Tools не получено или невалидно):");
                        foreach (var error in blockingErrors)
                            ed.WriteMessage($"\n  - {error}");
                        return;
                    }

                    if (nonBlockingErrors.Any())
                    {
                        ed.WriteMessage($"\n⚠️ Обнаружено {nonBlockingErrors.Count} замечаний (не блокируют отрисовку):");
                        var grouped = nonBlockingErrors
                            .GroupBy(e => e.Split(':')[0])
                            .Select(g => $"{g.Key}: {g.Count()} шт.");
                        foreach (var g in grouped)
                            ed.WriteMessage($"\n  - {g}");
                    }

                    if (result.OverConsumptionExceeded || nonBlockingErrors.Any(e => e.StartsWith("E_OVERCONSUMPTION")))
                    {
                        ed.WriteMessage($"\n⚠️ Решение найдено, но зафиксирован перерасход материала: {result.OverconsumptionPct:F1}%");
                    }

                    // Display OR-Tools specific info
                    ed.WriteMessage($"\n✅ Оптимизация завершена!");
                    ed.WriteMessage($"\n   Режим: {result.OptimizationMode}");
                    if (!string.IsNullOrEmpty(result.SolverStatus))
                        ed.WriteMessage($"\n   Статус: {result.SolverStatus}");
                    if (result.SolveTimeMs > 0)
                        ed.WriteMessage($"\n   Время решения: {result.SolveTimeMs} мс");

                    // Create layers and visualize
                    ed.WriteMessage("\nСоздание слоев и отрисовка...");
                    VisualizationEngine.CreateLayers(db, tr);
                    VisualizationEngine.CleanOldLayout(db, tr);

                    var visualizations = engine.CreateVisualizationObjects(result);
                    VisualizationEngine.VisualizeLayout(db, tr, visualizations);

                    if (result.HatchRegions.Any())
                    {
                        ed.WriteMessage($"\nШтриховка оконных перекрытий: {result.HatchRegions.Count} областей...");
                        VisualizationEngine.VisualizeHatchRegions(db, tr, result.HatchRegions);
                    }

                    VisualizationEngine.CreateLegend(db, tr, result);

                    if (result.FacadeInfos.Any())
                    {
                        ed.WriteMessage($"\nВизуальная маркировка фасадов: {result.FacadeInfos.Count} фасадов...");
                        VisualizationEngine.VisualizeFacadeLabels(db, tr, result.FacadeInfos, result.AllTiles);
                    }

                    DisplayStatistics(ed, result);

                    tr.Commit();

                    // [FIX] Принудительное обновление экрана — объекты появляются сразу после коммита
                    try { doc.Editor.Regen(); } catch { }

                    // Генерация Excel-отчёта движения остатков
                    try
                    {
                        ed.WriteMessage("\nГенерация Excel-отчёта движения остатков...");
                        ExcelMovementReporter.GenerateFromLayoutResult(result, initialStock);
                        ed.WriteMessage("\n📊 Excel-отчёт сохранён: C:\\DB_INS\\remnants.xlsx");
                    }
                    catch (System.Exception excelEx)
                    {
                        // [FIX] Log full exception to database.log
                        try
                        {
                            string logFile = @"C:\DB_INS\database.log";
                            string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Excel] [FIX] ОШИБКА генерации Excel:\n{excelEx}";
                            System.IO.File.AppendAllText(logFile, logEntry + Environment.NewLine);
                        }
                        catch { }

                        // [FIX] Plan B: External process ExcelGenerator.exe
                        ed.WriteMessage($"\n⚠️ ClosedXML не загружается в AutoCAD — запуск внешнего генератора...");
                        try
                        {
                            GenerateExcelViaExternalProcess(result, initialStock, ed);
                        }
                        catch (System.Exception extEx)
                        {
                            ed.WriteMessage($"\n❌ Внешний генератор тоже не удался: {extEx.Message}");
                            try
                            {
                                string logFile = @"C:\DB_INS\database.log";
                                System.IO.File.AppendAllText(logFile,
                                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Excel] [FIX] Внешний генератор ошибка: {extEx}\n");
                            }
                            catch { }
                        }
                    }

                    ed.WriteMessage(result.OverConsumptionExceeded
                        ? "\n\n⚠️ Раскладка завершена: решение найдено, но перерасход превышает лимит 7%."
                        : "\n\n✅ Раскладка с OR-Tools успешно завершена!");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ Критическая ошибка: {ex.Message}");
                ed.WriteMessage($"\nStack Trace: {ex.StackTrace}");
            }
        }

        [CommandMethod("INS_DEL")]
        public void CleanLayout()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    VisualizationEngine.CleanOldLayout(db, tr);
                    tr.Commit();
                    ed.WriteMessage("\n✅ Старые результаты раскладки удалены с чертежа.");
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ Ошибка: {ex.Message}");
            }
        }

        [CommandMethod("INS_STOCK")]
        public void ShowRemnantStats()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            try
            {
                var remnants = Data.RemnantDatabase.LoadRemnants();
                string stats = Data.RemnantManager.GetRemnantStatistics(remnants);
                ed.WriteMessage("\n=== Склад остатков ===");
                ed.WriteMessage($"\n{stats}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ Ошибка: {ex.Message}");
            }
        }

        [CommandMethod("INS_CLR")]
        public void ClearStock()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            try
            {
                Data.RemnantDatabase.ClearAll();
                ed.WriteMessage("\n✅ Склад очищен. Все остатки удалены из базы.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ Ошибка очистки склада: {ex.Message}");
            }
        }

        /// <summary>
        /// Импортирует начальный склад из листа «Нач. склад» файла C:\DB_INS\remnants.xlsx.
        /// Полностью заменяет текущий склад (неиспользованные остатки) на записи из Excel.
        /// Формат листа: столбцы Ширина (мм) | Высота (мм) | Количество (шт.)
        /// </summary>
        [CommandMethod("INS_STOCK_IMPORT")]
        public void ImportInitialStock()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;

            try
            {
                ed.WriteMessage("\n=== INS_STOCK_IMPORT: Импорт начального склада ===");
                string excelPath = @"C:\DB_INS\remnants.xlsx";

                if (!File.Exists(excelPath))
                {
                    ed.WriteMessage($"\n❌ Файл не найден: {excelPath}");
                    ed.WriteMessage("\nСоздайте файл командой INS (запустите раскладку) или скопируйте шаблон.");
                    return;
                }

                var newRemnants = new List<Data.RemnantImportRow>();

                try
                {
                    using var workbook = new XLWorkbook(excelPath);
                    const string sheetName = "Нач. склад";
                    if (!workbook.TryGetWorksheet(sheetName, out var ws))
                    {
                        ed.WriteMessage($"\n❌ Лист «{sheetName}» не найден в файле.");
                        ed.WriteMessage("\nВыполните INS чтобы создать шаблонный лист.");
                        return;
                    }

                    int headerRow = -1;
                    int colWidth = -1, colHeight = -1, colQty = -1;
                    for (int r = 1; r <= Math.Min(ws.LastRowUsed()?.RowNumber() ?? 10, 10); r++)
                    {
                        for (int c = 1; c <= 5; c++)
                        {
                            var val = ws.Cell(r, c).GetString().Trim();
                            if (val.Contains("Ширина") && val.Contains("мм")) { colWidth = c; headerRow = r; }
                            else if (val.Contains("Высота") && val.Contains("мм")) colHeight = c;
                            else if (val.Contains("Количество") || val.Contains("шт")) colQty = c;
                        }
                        if (headerRow == r) break;
                    }

                    if (headerRow < 0 || colWidth < 0 || colHeight < 0)
                    {
                        ed.WriteMessage("\n❌ Не удалось найти заголовки «Ширина (мм)» / «Высота (мм)» в листе.");
                        return;
                    }

                    int lastRow = ws.LastRowUsed()?.RowNumber() ?? headerRow;
                    int importedOk = 0, skipped = 0;

                    for (int r = headerRow + 1; r <= lastRow; r++)
                    {
                        string rawW = ws.Cell(r, colWidth).GetString().Trim();
                        string rawH = ws.Cell(r, colHeight).GetString().Trim();
                        if (string.IsNullOrEmpty(rawW) && string.IsNullOrEmpty(rawH)) continue;

                        if (!int.TryParse(rawW, out int w) || !int.TryParse(rawH, out int h))
                        {
                            ed.WriteMessage($"\n  ⚠️ Строка {r}: не удалось распознать размеры «{rawW}»×«{rawH}» — пропуск");
                            skipped++;
                            continue;
                        }
                        if (w < 150 || h < 150)
                        {
                            ed.WriteMessage($"\n  ⚠️ Строка {r}: {w}×{h} мм — размер < 150 мм — пропуск");
                            skipped++;
                            continue;
                        }

                        int qty = 1;
                        if (colQty > 0)
                        {
                            string rawQ = ws.Cell(r, colQty).GetString().Trim();
                            if (!string.IsNullOrEmpty(rawQ) && int.TryParse(rawQ, out int parsedQty) && parsedQty > 0)
                                qty = parsedQty;
                        }

                        newRemnants.Add(new Data.RemnantImportRow { Width = w, Height = h, Quantity = qty });
                        importedOk++;
                    }

                    ed.WriteMessage($"\n  Прочитано строк: {importedOk} (пропущено: {skipped})");
                }
                catch (System.Exception xlEx)
                {
                    ed.WriteMessage($"\n❌ Ошибка чтения Excel: {xlEx.Message}");
                    return;
                }

                if (newRemnants.Count == 0)
                {
                    ed.WriteMessage("\n⚠️ Нет допустимых строк для импорта. Склад не изменён.");
                    return;
                }

                Data.RemnantDatabase.BackupDatabase();
                var oldRemnants = Data.RemnantDatabase.LoadRemnants();
                int oldCount = oldRemnants.Count(r => !r.IsUsed);

                var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var remnants = new List<Models.Remnant>();
                int seq = 1;
                foreach (var row in newRemnants)
                {
                    for (int q = 0; q < row.Quantity; q++)
                    {
                        remnants.Add(new Models.Remnant
                        {
                            Id = Guid.NewGuid(),
                            Width = row.Width,
                            Height = row.Height,
                            Source = $"IMPORT_{timestamp}_{seq:D3}",
                            AddedDate = DateTime.Now,
                            IsUsed = false
                        });
                        seq++;
                    }
                }

                Data.RemnantDatabase.ReplaceUnusedRemnants(remnants);

                ed.WriteMessage($"\n✅ Склад обновлён: импортировано {remnants.Count} остатков. Предыдущих (неисп.) удалено: {oldCount}.");
                ed.WriteMessage("\n📋 Для проверки склада используйте INS_STOCK.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ Критическая ошибка импорта: {ex.Message}");
            }
        }

        [CommandMethod("INS_CHECK")]
        public void ValidateContours()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                ed.WriteMessage("\n=== Валидация контуров ===");

                var facadeIds = FindClosedContourIds(db, ed, "Внешний контур фасада");
                var windowIds = FindClosedContourIds(db, ed, "Контуры окон");

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var facades = GetPolylinesFromIds(tr, facadeIds);
                    var windows = GetPolylinesFromIds(tr, windowIds);

                    var facadeValidation = ValidationEngine.ValidateMultipleContours(facades, "Фасад");
                    var windowValidation = windows.Count == 0
                        ? new Validation.ValidationResult { IsValid = true }
                        : ValidationEngine.ValidateMultipleContours(windows, "Окно");

                    ed.WriteMessage($"\nФасады: {(facadeValidation.IsValid ? "✅" : "❌")}");
                    foreach (var error in facadeValidation.Errors)
                        ed.WriteMessage($"\n  ❌ {error}");
                    foreach (var warning in facadeValidation.Warnings)
                        ed.WriteMessage($"\n  ⚠️ {warning}");

                    ed.WriteMessage($"\nОкна: {(windowValidation.IsValid ? "✅" : "❌")}");
                    foreach (var error in windowValidation.Errors)
                        ed.WriteMessage($"\n  ❌ {error}");
                    foreach (var warning in windowValidation.Warnings)
                        ed.WriteMessage($"\n  ⚠️ {warning}");

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n❌ Ошибка при валидации: {ex.Message}");
            }
        }

        /// <summary>
        /// Возвращает ObjectId замкнутых полилиний на указанном слое (для последующего открытия в той же транзакции).
        /// </summary>
        private static List<ObjectId> FindClosedContourIds(Database db, Editor ed, string layerName)
        {
            var ids = new List<ObjectId>();
            var filter = new TypedValue[] {
                new TypedValue(0, "LWPOLYLINE"),
                new TypedValue(8, layerName)
            };
            var sf = new SelectionFilter(filter);
            var sr = ed.SelectAll(sf);

            if (sr.Status != PromptStatus.OK) return ids;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject obj in sr.Value)
                {
                    var pl = tr.GetObject(obj.ObjectId, OpenMode.ForRead) as Polyline;
                    if (pl != null && pl.Closed)
                        ids.Add(obj.ObjectId);
                }
                tr.Commit();
            }
            return ids;
        }

        /// <summary>
        /// Открывает полилинии по ObjectId в текущей транзакции.
        /// </summary>
        private static List<Polyline> GetPolylinesFromIds(Transaction tr, List<ObjectId> ids)
        {
            var list = new List<Polyline>();
            foreach (var id in ids)
            {
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                if (pl != null) list.Add(pl);
            }
            return list;
        }

        /// <summary>
        /// Ошибки, которые не должны блокировать отрисовку: решение найдено,
        /// но есть замечания по качеству (L-boot полки, перерасход).
        /// Блокирующие ошибки: "OR-Tools: решение не найдено", критические ошибки.
        /// </summary>
        private static bool IsNonBlockingError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return false;
            return error.StartsWith("E_OVERCONSUMPTION", StringComparison.OrdinalIgnoreCase) ||
                   error.StartsWith("E_LBOOT_SHELF_SIZE", StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteDiagnosticsSafe(Editor ed, LayoutResult result, List<Remnant> initialStock)
        {
            try
            {
                if (result.DiagInput != null && result.DiagSolution != null)
                {
                    DiagnosticLogger.WriteDiagnostics(
                        result.DiagInput, result.DiagSolution, result, initialStock);
                    ed.WriteMessage("\n🔍 Диагностика: C:\\DB_INS\\diagnostics.txt");
                }
                else
                {
                    // [FIX] Log which field is null for debugging
                    LogFix(ed, $"Диагностика не сформирована: DiagInput={result.DiagInput != null}, DiagSolution={result.DiagSolution != null}, AllTiles={result.AllTiles.Count}, Errors={result.Errors.Count}");
                    if (result.Errors.Any())
                    {
                        foreach (var err in result.Errors.Take(5))
                            LogFix(ed, $"  Error: {err}");
                    }
                }
            }
            catch (System.Exception diagEx)
            {
                ed.WriteMessage($"\n⚠️ Ошибка диагностики: {diagEx.Message}");
            }
        }

        /// <summary>
        /// [FIX] Logging helper — writes to both AutoCAD command line and C:\DB_INS\database.log
        /// </summary>
        private static void LogFix(Editor ed, string message)
        {
            string formatted = $"[FIX] {message}";
            ed.WriteMessage($"\n{formatted}");
            try
            {
                string logFile = @"C:\DB_INS\database.log";
                System.IO.File.AppendAllText(logFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {formatted}{Environment.NewLine}");
            }
            catch { }
        }



        /// <summary>
        /// Вывод статистики в командную строку
        /// </summary>
        private void DisplayStatistics(Editor ed, LayoutResult result)
        {
            ed.WriteMessage("\n=== СТАТИСТИКА РАСКЛАДКИ ===");
            ed.WriteMessage($"\nНовые плиты: {result.NewTilesCount} шт. ({result.NewTilesArea:F2} м²)");
            ed.WriteMessage($"\nИспользовано остатков: {result.ReusedRemnantsCount} шт. ({result.ReusedArea:F2} м²)");
            ed.WriteMessage($"\nОбщая площадь утепления: {result.TotalInsulationArea:F2} м²");
            ed.WriteMessage($"\nОтходы при раскрое: {result.WasteArea:F2} м²");
            ed.WriteMessage($"\nКоэффициент использования: {result.UtilizationRatio:P1}");
            if (result.CreatedRemnants.Any())
                ed.WriteMessage($"\nСоздано остатков (в БД): {result.CreatedRemnants.Count} шт.");

            // Материальный баланс
            ed.WriteMessage("\n--- МАТЕРИАЛЬНЫЙ БАЛАНС ---");
            ed.WriteMessage($"\nЧистая площадь утепления (100%): {result.NetInsulationArea:F3} м²");
            ed.WriteMessage($"\nНовый материал: {result.NewTilesCount} плит = {result.TotalNewMaterialArea:F3} м²");
            ed.WriteMessage($"\nПерерасход: +{result.OverconsumptionPct:F1}%  КПД раскроя: {result.CuttingEfficiencyPct:F1}%");
            if (result.StockSavingsPct > 0)
                ed.WriteMessage($"\nЭкономия от остатков: {result.StockSavingsPct:F1}%");

            foreach (var warning in result.Warnings.Take(3))
            {
                ed.WriteMessage($"\n⚠️ {warning}");
            }

            // FIX v3.1.0: предупреждение о строках ниже MinRowHeight
            if (result.DiagInput != null)
            {
                var tooShortRows = result.DiagInput.Rows
                    .Where(r => r.Height < 150 - 0.1)
                    .ToList();
                if (tooShortRows.Any())
                {
                    ed.WriteMessage($"\n⚠️ НАРУШЕНИЕ C3: {tooShortRows.Count} строк(а) высотой < 150мм: " +
                        string.Join(", ", tooShortRows.Select(r => $"{r.Height:F0}мм")));
                }
            }
        }
        /// <summary>
        /// План Б: генерация Excel через внешний процесс ExcelGenerator.exe.
        /// ClosedXML не загружается в AutoCAD из-за LoadFromResolveHandler (0x80131621),
        /// но отлично работает в отдельном .NET процессе.
        /// </summary>
        private static void GenerateExcelViaExternalProcess(
            LayoutResult result,
            List<Remnant> initialStock,
            Autodesk.AutoCAD.EditorInput.Editor ed)
        {
            // 1. Подготовить данные (зеркало ExcelMovementReporter.GenerateFromLayoutResult)
            var usedIds = result.AllTiles
                .Where(t => t.IsReused && t.SourceRemnantId.HasValue)
                .Select(t => t.SourceRemnantId!.Value)
                .Distinct()
                .ToList();

            var allRemnants = new List<Remnant>(initialStock);
            allRemnants.AddRange(result.CreatedRemnants);

            var createdRemnants = result.CreatedRemnants;
            var stats = new LayoutStatisticsDTO
            {
                SolverStatus = result.SolverStatus,
                SolveTimeMs = result.SolveTimeMs,
                NewTilesArea = result.NewTilesArea,
                NewTilesCount = result.NewTilesCount,
                ReusedArea = result.ReusedArea,
                ReusedRemnantsCount = result.ReusedRemnantsCount,
                TotalInsulationArea = result.TotalInsulationArea,
                WasteFromNewTilesArea = result.WasteFromNewTilesArea,
                WasteFromRemnantsArea = result.WasteFromRemnantsArea,
                WasteArea = result.WasteArea,
                UtilizationRatio = result.UtilizationRatio,
                FacadeGrossArea = result.FacadeGrossArea,
                WindowsArea = result.WindowsArea,
                WindowsCount = result.WindowsCount,
                NetInsulationArea = result.NetInsulationArea,
                TotalNewMaterialArea = result.TotalNewMaterialArea,
                CreatedRemnantsArea = result.CreatedRemnantsArea,
                OverconsumptionPct = result.OverconsumptionPct,
                OverConsumptionExceeded = result.OverConsumptionExceeded,
                CuttingEfficiencyPct = result.CuttingEfficiencyPct,
                StockSavingsPct = result.StockSavingsPct,
                WasteDisposalCount = createdRemnants.Count(r => r.Fate == PieceFate.Waste),
                WasteDisposalAreaM2 = createdRemnants.Where(r => r.Fate == PieceFate.Waste).Sum(r => r.Area) / 1_000_000.0,
                CreatedRemnantsCount = createdRemnants.DistinctBy(r => r.Id).Count(),
                CreatedRemnantsToStockCount = createdRemnants.DistinctBy(r => r.Id)
                    .Count(r => r.Source != "Создан и переиспользован" && r.Fate != PieceFate.Waste),
                CreatedRemnantsReusedCount = createdRemnants.DistinctBy(r => r.Id)
                    .Count(r => r.Source == "Создан и переиспользован")
            };

            var diagnostics = result.Warnings
                .Where(w => w.StartsWith("E_") || w.Contains("ошибка") || w.Contains("Ошибка"))
                .ToList();

            // 2. Сериализовать в JSON
            double SafePositionX(TileInfo t)
            {
                try { return t.Position.X; }
                catch { return 0; }
            }

            var inputDto = new
            {
                InitialStock = initialStock.Select(r => new
                {
                    r.Id, r.Width, r.Height, r.AddedDate, r.IsUsed, r.Source,
                    r.ParentRemnantId, r.BaseBlockNumber, r.SuffixCounter, r.CutHistory, r.SourceBlockId
                }),
                UsedFromStockIds = usedIds,
                CreatedRemnants = result.CreatedRemnants.Select(r => new
                {
                    r.Id, r.Width, r.Height, r.AddedDate, r.IsUsed, r.Source,
                    r.ParentRemnantId, r.BaseBlockNumber, r.SuffixCounter, r.CutHistory, r.SourceBlockId
                }),
                AllRemnants = allRemnants.Select(r => new
                {
                    r.Id, r.Width, r.Height, r.AddedDate, r.IsUsed, r.Source,
                    r.ParentRemnantId, r.BaseBlockNumber, r.SuffixCounter, r.CutHistory, r.SourceBlockId
                }),
                Stats = stats,
                Diagnostics = diagnostics.Any() ? diagnostics : null,
                AllTiles = result.AllTiles.Select(t => new
                {
                    t.BlockNumber, t.RowIndex, t.SegmentIndex,
                    PositionX = SafePositionX(t),
                    t.Width, t.Height, t.IsReused, t.ParentInfo,
                    t.SourceRemnantWidth, t.SourceRemnantHeight
                })
            };

            string jsonPath = @"C:\DB_INS\excel_input.json";
            var jsonSettings = new Newtonsoft.Json.JsonSerializerSettings
            {
                Formatting = Newtonsoft.Json.Formatting.Indented,
                DateFormatString = "yyyy-MM-ddTHH:mm:ss"
            };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(inputDto, jsonSettings);
            System.IO.File.WriteAllText(jsonPath, json);

            // 3. Найти ExcelGenerator.exe рядом с плагином
            string pluginDir = System.IO.Path.GetDirectoryName(
                typeof(PluginInitializer).Assembly.Location) ?? "";
            string exePath = System.IO.Path.Combine(pluginDir, "ExcelGenerator.exe");

            if (!System.IO.File.Exists(exePath))
            {
                throw new System.IO.FileNotFoundException(
                    $"ExcelGenerator.exe не найден: {exePath}");
            }

            // 4. Запустить внешний процесс
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"\"{jsonPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                throw new System.Exception("Не удалось запустить ExcelGenerator.exe");

            process.WaitForExit(30_000); // 30 сек таймаут

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (process.ExitCode == 0)
            {
                ed.WriteMessage("\n📊 Excel-отчёт сгенерирован (внешний процесс): C:\\DB_INS\\remnants.xlsx");
            }
            else
            {
                throw new System.Exception(
                    $"ExcelGenerator.exe вернул код {process.ExitCode}. stderr: {stderr}");
            }
        }
    }
}
