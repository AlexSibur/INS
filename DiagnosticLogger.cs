using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using InsulationMasterPro.Models;
using InsulationMasterPro.OrTools;

namespace InsulationMasterPro.Data
{
    /// <summary>
    /// Полная runtime-диагностика работы плагина.
    /// Создаёт diagnostics.txt (человекочитаемый) и diagnostics.json (машинный) при каждом запуске INS.
    /// Файлы перезаписываются — всегда содержат данные ПОСЛЕДНЕГО запуска.
    /// </summary>
    public static class DiagnosticLogger
    {
        private static readonly string DbDir = @"C:\DB_INS";
        private static readonly string TxtPath = @"C:\DB_INS\diagnostics.txt";
        private static readonly string JsonPath = @"C:\DB_INS\diagnostics.json";
        private const string Version = "3.5.0";

        /// <summary>
        /// Записывает полную диагностику раскладки в TXT и JSON.
        /// </summary>
        public static void WriteDiagnostics(
            FacadeInput input,
            OrToolsSolution solution,
            LayoutResult result,
            List<Remnant> initialStock,
            long preprocessTimeMs = 0)
        {
            try
            {
                if (!Directory.Exists(DbDir))
                    Directory.CreateDirectory(DbDir);

                var timestamp = DateTime.Now;
                WriteTxt(input, solution, result, initialStock, timestamp, preprocessTimeMs);
                WriteJson(input, solution, result, initialStock, timestamp, preprocessTimeMs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DIAG] Error writing diagnostics: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  TXT — 10 секций
        // ──────────────────────────────────────────────────────────────

        private static void WriteTxt(
            FacadeInput input, OrToolsSolution solution, LayoutResult result,
            List<Remnant> initialStock, DateTime timestamp, long preprocessTimeMs)
        {
            var sb = new StringBuilder();

            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("  INSULATION MASTER PRO — ПОЛНАЯ ДИАГНОСТИКА");
            sb.AppendLine($"  Версия: v{Version}  |  Дата: {timestamp:dd.MM.yyyy HH:mm:ss}");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine();

            // § 1. ВХОДНЫЕ ДАННЫЕ
            WriteSection(sb, "1. ВХОДНЫЕ ДАННЫЕ");
            sb.AppendLine($"  Фасад: {input.Width:F0} × {input.Height:F0} мм (MinX={input.MinX:F0}, MinY={input.MinY:F0})");
            sb.AppendLine($"  FacadeId: {input.FacadeId}  FacadeIndex: {input.FacadeIndex}");
            sb.AppendLine($"  Окна: {input.Windows.Count} шт.");
            foreach (var w in input.Windows)
                sb.AppendLine($"    [{w.Id}] {w.Width:F0}×{w.Height:F0} мм  X={w.MinX:F0}..{w.MaxX:F0}  Y={w.MinY:F0}..{w.MaxY:F0}");
            sb.AppendLine($"  Склад остатков (до): {initialStock.Count} шт., {initialStock.Sum(r => r.Area) / 1_000_000.0:F3} м²");
            foreach (var r in initialStock.OrderByDescending(r => r.Area).Take(20))
            {
                string lineage = !string.IsNullOrEmpty(r.BaseBlockNumber) ? $" (от {r.BaseBlockNumber}.{r.SuffixCounter:D2})" : "";
                sb.AppendLine($"    [{r.Id.ToString().Substring(0, 8)}] {r.Width:F0}×{r.Height:F0} мм{lineage}");
            }
            if (initialStock.Count > 20) sb.AppendLine($"    ... и ещё {initialStock.Count - 20} шт.");
            sb.AppendLine();

            // § 2. ПАРАМЕТРЫ ОПТИМИЗАЦИИ
            WriteSection(sb, "2. ПАРАМЕТРЫ ОПТИМИЗАЦИИ");
            var c = input.Constraints;
            sb.AppendLine($"  TileWidth={c.TileWidth:F0}  TileHeight={c.TileHeight:F0}");
            sb.AppendLine($"  MinBlock={c.MinBlock:F0}  MinBlockNearWindow={c.MinBlockNearWindow:F0}");
            sb.AppendLine($"  MinJointOffset={c.MinJointOffset:F0}  ReleaseOverhang={c.ReleaseOverhang:F0}");
            sb.AppendLine($"  MinReleasePiece={c.MinReleasePiece:F0}");
            sb.AppendLine($"  MinRowHeight={c.MinRowHeight:F0}  MinRemnantToSave={c.MinRemnantToSave:F0}");
            sb.AppendLine($"  WindowOverlap={c.WindowOverlap:F0}  CornerZone={c.CornerZone:F0}");
            sb.AppendLine($"  MaxConsecutiveRemnants={c.MaxConsecutiveRemnants}  MaxConsecutiveRemnantsWindow={c.MaxConsecutiveRemnantsWindow}");
            sb.AppendLine($"  FirstRowWithRelease={input.FirstRowWithRelease}");
            sb.AppendLine();

            // § 3. ДЕЛЕНИЕ НА РЯДЫ
            WriteSection(sb, "3. ДЕЛЕНИЕ НА РЯДЫ");
            sb.AppendLine($"  Количество рядов: {input.Rows.Count}");
            foreach (var row in input.Rows)
            {
                sb.AppendLine($"  Ряд {row.RowIndex}: H={row.Height:F0}  Y={row.Y:F0}  " +
                              $"Type={row.RowType}  Release={row.HasRelease}  Segments={row.Segments.Count}");
                foreach (var seg in row.Segments)
                    sb.AppendLine($"    Seg[{seg.SegmentIndex}]: X={seg.StartX:F0}..{seg.EndX:F0} W={seg.Width:F0} " +
                                  $"WinL={seg.NearWindowLeft} WinR={seg.NearWindowRight} RelL={seg.IsReleaseLeft} RelR={seg.IsReleaseRight}");
            }
            sb.AppendLine();

            // § 4. SOLVER
            WriteSection(sb, "4. SOLVER");
            sb.AppendLine($"  Статус: {solution.Status}");
            sb.AppendLine($"  Время решения: {solution.SolveTimeMs} мс");
            sb.AppendLine($"  Objective: {solution.ObjectiveValue:F2}");
            sb.AppendLine($"  Preprocess: {preprocessTimeMs} мс");
            if (solution.Diagnostics.Any())
            {
                sb.AppendLine($"  Диагностика солвера ({solution.Diagnostics.Count}):");
                foreach (var d in solution.Diagnostics)
                    sb.AppendLine($"    {d}");
            }
            sb.AppendLine();

            // § 5. РАСКЛАДКА ПО РЯДАМ
            WriteSection(sb, "5. РАСКЛАДКА ПО РЯДАМ");
            foreach (var row in solution.Rows)
            {
                sb.AppendLine($"  Ряд {row.RowIndex} (H={row.Height:F0}  Y={row.Y:F0}):");
                foreach (var seg in row.Segments)
                {
                    sb.AppendLine($"    Сегмент [{seg.SegmentIndex}] X={seg.StartX:F0}..{seg.EndX:F0}:");
                    double x = seg.StartX;
                    foreach (var block in seg.Blocks)
                    {
                        string type = block.Type.ToString().PadRight(10);
                        string remnantInfo = block.RemnantId.HasValue ? $" RemnantId={block.RemnantId.Value.ToString().Substring(0, 8)}" : "";
                        string rotated = block.IsRotated ? " ROT" : "";
                        sb.AppendLine($"      [{type}] W={block.Width:F0}  X={x:F0}{remnantInfo}{rotated}");
                        x += block.Width;
                    }
                }
            }
            sb.AppendLine();

            // § 6. МАРКИРОВКА (формат X.Y.NNN / X.Y.NNN.ZZ)
            WriteSection(sb, "6. МАРКИРОВКА");
            var newTiles = result.AllTiles.Where(t => !t.IsReused).ToList();
            var lineageTiles = result.AllTiles.Where(t => t.IsReused && t.BlockNumber.Split('.').Length >= 4).ToList();
            var noHistoryTiles = result.AllTiles.Where(t => t.IsReused && t.BlockNumber.Split('.').Length < 4).ToList();
            sb.AppendLine($"  Новые плиты (X.Y.NNN): {newTiles.Count} шт.");
            if (newTiles.Any())
                sb.AppendLine($"    {string.Join(", ", newTiles.Select(t => t.BlockNumber).Take(30))}{(newTiles.Count > 30 ? "..." : "")}");
            sb.AppendLine($"  Остатки с lineage (X.Y.NNN.ZZ): {lineageTiles.Count} шт.");
            if (lineageTiles.Any())
                sb.AppendLine($"    {string.Join(", ", lineageTiles.Select(t => t.BlockNumber))}");
            sb.AppendLine($"  Остатки без истории: {noHistoryTiles.Count} шт.");
            if (noHistoryTiles.Any())
                sb.AppendLine($"    {string.Join(", ", noHistoryTiles.Select(t => t.BlockNumber))}");
            sb.AppendLine($"  Всего на чертеже: {result.AllTiles.Count} плит");
            sb.AppendLine();

            // § 7. МАТЕРИАЛЬНЫЙ БАЛАНС
            WriteSection(sb, "7. МАТЕРИАЛЬНЫЙ БАЛАНС");
            sb.AppendLine($"  Площадь фасада (брутто): {result.FacadeGrossArea:F3} м²");
            sb.AppendLine($"  Площадь проёмов: {result.WindowsArea:F3} м²");
            sb.AppendLine($"  Чистая площадь утепления (100%): {result.NetInsulationArea:F3} м²");
            sb.AppendLine($"  Новый материал: {result.NewTilesCount} × {c.TileWidth:F0}×{c.TileHeight:F0} = {result.TotalNewMaterialArea:F3} м²");
            sb.AppendLine($"    → уложено на фасад: {result.NewTilesArea:F3} м²");
            sb.AppendLine($"    → на склад (остатки): {result.CreatedRemnantsArea:F3} м²");
            sb.AppendLine($"    → в отходы: {result.WasteFromNewTilesArea:F3} м²");
            sb.AppendLine($"  Остатки со склада: {result.ReusedRemnantsCount} шт. ({result.ReusedArea:F3} м²)");
            sb.AppendLine($"  Отходы от раскроя остатков: {result.WasteFromRemnantsArea:F3} м²");
            sb.AppendLine($"  Суммарные отходы: {result.WasteArea:F3} м² ({result.WastePct:F1}%)");
            sb.AppendLine($"  Перерасход: +{result.OverconsumptionPct:F1}%");
            sb.AppendLine($"  Лимит перерасхода (7%): {(result.OverConsumptionExceeded ? "ПРЕВЫШЕН" : "OK")}");
            sb.AppendLine($"  КПД раскроя: {result.CuttingEfficiencyPct:F1}%");
            sb.AppendLine($"  Экономия от остатков: {result.StockSavingsPct:F1}%");
            sb.AppendLine($"  Коэффициент использования: {result.UtilizationRatio:P1}");

            // Разбивка созданных остатков по категориям
            var fromNew = result.CreatedRemnants.Where(r => r.Source == "Раскрой новой плиты").ToList();
            var fromCut = result.CreatedRemnants.Where(r => r.Source == "Раскрой остатка").ToList();
            var reused  = result.CreatedRemnants.Where(r => r.Source == "Создан и переиспользован").ToList();
            sb.AppendLine();
            sb.AppendLine($"  --- Разбивка созданных остатков ---");
            sb.AppendLine($"  От новых плит:      {fromNew.Count} шт. ({fromNew.Sum(r => r.Area) / 1_000_000.0:F3} м²)");
            sb.AppendLine($"  От раскроя остатка: {fromCut.Count} шт. ({fromCut.Sum(r => r.Area) / 1_000_000.0:F3} м²)");
            sb.AppendLine($"  Переиспользованы:   {reused.Count} шт. ({reused.Sum(r => r.Area) / 1_000_000.0:F3} м²) [не сохраняются в БД]");

            double expected = result.TotalNewMaterialArea;
            double actual = result.NewTilesArea + result.WasteFromNewTilesArea + result.CreatedRemnantsArea;
            double gap = Math.Abs(expected - actual);
            sb.AppendLine();
            sb.AppendLine($"  Проверка баланса: {(gap > 0.01 ? $"[ОШИБКА: gap = {gap:F4} м²]" : "[OK]")}");

            // Предупреждение при перерасходе > 7%
            if (result.OverConsumptionExceeded)
                sb.AppendLine($"  ⚠️ ПЕРЕРАСХОД {result.OverconsumptionPct:F1}% > 7% — рекомендуется оптимизация!");
            sb.AppendLine();

            // § 8. СКЛАД ПОСЛЕ РАСКЛАДКИ (ДВИЖЕНИЕ ОСТАТКОВ)
            WriteSection(sb, "8. СКЛАД ПОСЛЕ РАСКЛАДКИ (ДВИЖЕНИЕ ОСТАТКОВ)");
            var usedFromStock = initialStock.Where(r =>
                result.AllTiles.Any(t => t.IsReused && t.SourceRemnantId == r.Id)).ToList();
            sb.AppendLine($"  Использовано со склада: {usedFromStock.Count} шт.");
            foreach (var r in usedFromStock)
            {
                string displayNum = FormatRemnantDisplayNumber(r);
                sb.AppendLine($"    [{displayNum}] {r.Width:F0}×{r.Height:F0} мм → ИСПОЛЬЗОВАН");
            }

            // Остатки, которые будут сохранены в БД (без переиспользованных)
            var savedToDB = result.CreatedRemnants.Where(r => r.Source != "Создан и переиспользован").ToList();
            sb.AppendLine($"  Создано при раскрое (→ БД): {savedToDB.Count} шт.");
            foreach (var r in savedToDB.OrderByDescending(r => r.Area).Take(30))
            {
                string displayNum = FormatRemnantDisplayNumber(r);
                sb.AppendLine($"    [{displayNum}] {r.Width:F0}×{r.Height:F0} мм  Источник: {r.Source}");
            }
            if (savedToDB.Count > 30)
                sb.AppendLine($"    ... и ещё {savedToDB.Count - 30} шт.");

            // Переиспользованные (для информации)
            if (reused.Any())
            {
                sb.AppendLine($"  Создано и переиспользовано (не в БД): {reused.Count} шт.");
                foreach (var r in reused)
                {
                    string displayNum = FormatRemnantDisplayNumber(r);
                    sb.AppendLine($"    [{displayNum}] {r.Width:F0}×{r.Height:F0} мм  → утилизирован в раскладке");
                }
            }

            int stockBefore = initialStock.Count;
            int stockAfterCount = stockBefore - usedFromStock.Count + savedToDB.Count;
            double stockAreaBefore = initialStock.Sum(r => r.Area) / 1_000_000.0;
            double stockAreaAfter = (initialStock.Where(r => !usedFromStock.Contains(r)).Sum(r => r.Area)
                + savedToDB.Sum(r => r.Area)) / 1_000_000.0;
            sb.AppendLine($"  Итого на складе после: {stockAfterCount} шт. ({stockAreaAfter:F3} м²)");
            sb.AppendLine($"  Δ склад: {stockAfterCount - stockBefore:+#;-#;0} шт., {stockAreaAfter - stockAreaBefore:+0.000;-0.000;0.000} м²");
            if (stockAfterCount > stockBefore)
                sb.AppendLine($"  ⚠️ Склад ВЫРОС на {stockAfterCount - stockBefore} шт. — возможно накопление неликвида");
            sb.AppendLine();

            // § 9. ВАЛИДАЦИЯ ОГРАНИЧЕНИЙ
            WriteSection(sb, "9. ВАЛИДАЦИЯ ОГРАНИЧЕНИЙ");
            ValidateAndLog(sb, result, input, solution);
            sb.AppendLine();

            // § 10. ПРЕДУПРЕЖДЕНИЯ И ОШИБКИ
            WriteSection(sb, "10. ПРЕДУПРЕЖДЕНИЯ И ОШИБКИ");
            if (result.Errors.Any())
            {
                sb.AppendLine($"  ОШИБКИ ({result.Errors.Count}):");
                foreach (var e in result.Errors) sb.AppendLine($"    ❌ {e}");
            }
            if (result.Warnings.Any())
            {
                sb.AppendLine($"  ПРЕДУПРЕЖДЕНИЯ ({result.Warnings.Count}):");
                foreach (var w in result.Warnings) sb.AppendLine($"    ⚠️ {w}");
            }
            if (!result.Errors.Any() && !result.Warnings.Any())
                sb.AppendLine("  Нет ошибок и предупреждений.");

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════════════════════════");
            sb.AppendLine("  END OF DIAGNOSTICS");
            sb.AppendLine("═══════════════════════════════════════════════════════════════");

            File.WriteAllText(TxtPath, sb.ToString(), Encoding.UTF8);
        }

        // ──────────────────────────────────────────────────────────────
        //  JSON — зеркало TXT для машинного парсинга
        // ──────────────────────────────────────────────────────────────

        private static void WriteJson(
            FacadeInput input, OrToolsSolution solution, LayoutResult result,
            List<Remnant> initialStock, DateTime timestamp, long preprocessTimeMs)
        {
            var diag = new
            {
                version = Version,
                timestamp = timestamp.ToString("yyyy-MM-ddTHH:mm:ss"),
                input = new
                {
                    facadeId = input.FacadeId,
                    facadeIndex = input.FacadeIndex,
                    facadeWidth = input.Width,
                    facadeHeight = input.Height,
                    minX = input.MinX,
                    minY = input.MinY,
                    windowCount = input.Windows.Count,
                    windows = input.Windows.Select(w => new { w.Id, w.MinX, w.MinY, w.MaxX, w.MaxY, w.Width, w.Height }),
                    firstRowWithRelease = input.FirstRowWithRelease
                },
                constraints = new
                {
                    tileWidth = input.Constraints.TileWidth,
                    tileHeight = input.Constraints.TileHeight,
                    minBlock = input.Constraints.MinBlock,
                    minBlockNearWindow = input.Constraints.MinBlockNearWindow,
                    minJointOffset = input.Constraints.MinJointOffset,
                    releaseOverhang = input.Constraints.ReleaseOverhang,
                    minReleasePiece = input.Constraints.MinReleasePiece,
                    minRowHeight = input.Constraints.MinRowHeight,
                    minRemnantToSave = input.Constraints.MinRemnantToSave,
                    windowOverlap = input.Constraints.WindowOverlap,
                    cornerZone = input.Constraints.CornerZone,
                    maxConsecutiveRemnants = input.Constraints.MaxConsecutiveRemnants,
                    maxConsecutiveRemnantsWindow = input.Constraints.MaxConsecutiveRemnantsWindow
                },
                initialStock = initialStock.Select(r => new
                {
                    id = r.Id.ToString().Substring(0, 8),
                    width = r.Width,
                    height = r.Height,
                    areaM2 = r.Area / 1_000_000.0,
                    baseBlockNumber = r.BaseBlockNumber,
                    suffixCounter = r.SuffixCounter
                }),
                rows = input.Rows.Select(r => new
                {
                    index = r.RowIndex,
                    height = r.Height,
                    y = r.Y,
                    rowType = r.RowType.ToString(),
                    hasRelease = r.HasRelease,
                    segments = r.Segments.Select(s => new
                    {
                        index = s.SegmentIndex,
                        startX = s.StartX,
                        endX = s.EndX,
                        width = s.Width,
                        nearWindowLeft = s.NearWindowLeft,
                        nearWindowRight = s.NearWindowRight
                    })
                }),
                solver = new
                {
                    status = solution.Status.ToString(),
                    solveTimeMs = solution.SolveTimeMs,
                    objective = solution.ObjectiveValue,
                    preprocessTimeMs = preprocessTimeMs,
                    diagnostics = solution.Diagnostics
                },
                tiles = result.AllTiles.Select(t => new
                {
                    blockNumber = t.BlockNumber,
                    displayNumber = t.DisplayNumber,
                    width = t.Width,
                    height = t.Height,
                    isReused = t.IsReused,
                    hasRelease = t.HasRelease,
                    parentInfo = t.ParentInfo,
                    sourceRemnantId = t.SourceRemnantId?.ToString()?.Substring(0, 8),
                    sourceRemnantWidth = t.IsReused ? t.SourceRemnantWidth : 0,
                    sourceRemnantHeight = t.IsReused ? t.SourceRemnantHeight : 0
                }),
                marking = new
                {
                    format = "X.Y.NNN / X.Y.NNN.ZZ",
                    newCount = result.AllTiles.Count(t => !t.IsReused),
                    lineageCount = result.AllTiles.Count(t => t.IsReused && t.BlockNumber.Split('.').Length >= 4),
                    noHistoryCount = result.AllTiles.Count(t => t.IsReused && t.BlockNumber.Split('.').Length < 4),
                    total = result.AllTiles.Count
                },
                materialBalance = new
                {
                    facadeGrossAreaM2 = result.FacadeGrossArea,
                    windowsAreaM2 = result.WindowsArea,
                    windowsCount = result.WindowsCount,
                    netInsulationAreaM2 = result.NetInsulationArea,
                    totalNewMaterialAreaM2 = result.TotalNewMaterialArea,
                    createdRemnantsAreaM2 = result.CreatedRemnantsArea,
                    overconsumptionPct = result.OverconsumptionPct,
                    overLimitWarning = result.OverConsumptionExceeded,
                    cuttingEfficiencyPct = result.CuttingEfficiencyPct,
                    stockSavingsPct = result.StockSavingsPct,

                    newTilesCount = result.NewTilesCount,
                    newTilesAreaM2 = result.NewTilesArea,
                    wasteFromNewM2 = result.WasteFromNewTilesArea,
                    reusedCount = result.ReusedRemnantsCount,
                    reusedAreaM2 = result.ReusedArea,
                    wasteFromRemnantsM2 = result.WasteFromRemnantsArea,
                    totalWasteAreaM2 = result.WasteArea,
                    wastePct = result.WastePct,
                    utilizationRatio = result.UtilizationRatio
                },
                createdRemnantsBreakdown = new
                {
                    total = result.CreatedRemnants.DistinctBy(r => r.Id).Count(),
                    fromNewTiles = result.CreatedRemnants.DistinctBy(r => r.Id)
                        .Count(r => r.Source == "Раскрой новой плиты"),
                    fromNewTilesAreaM2 = result.CreatedRemnants.DistinctBy(r => r.Id)
                        .Where(r => r.Source == "Раскрой новой плиты").Sum(r => r.Area) / 1_000_000.0,
                    fromRemnantCut = result.CreatedRemnants.DistinctBy(r => r.Id)
                        .Count(r => r.Source == "Раскрой остатка"),
                    fromRemnantCutAreaM2 = result.CreatedRemnants.DistinctBy(r => r.Id)
                        .Where(r => r.Source == "Раскрой остатка").Sum(r => r.Area) / 1_000_000.0,
                    reusedInLayout = result.CreatedRemnants.DistinctBy(r => r.Id)
                        .Count(r => r.Source == "Создан и переиспользован"),
                    toStock = result.CreatedRemnants.DistinctBy(r => r.Id)
                        .Count(r => r.Source != "Создан и переиспользован" && r.Fate != PieceFate.Waste),
                    wasteDisposal = new
                    {
                        count = result.CreatedRemnants.DistinctBy(r => r.Id).Count(r => r.Fate == PieceFate.Waste),
                        areaM2 = result.CreatedRemnants.DistinctBy(r => r.Id)
                            .Where(r => r.Fate == PieceFate.Waste).Sum(r => r.Area) / 1_000_000.0,
                        items = result.CreatedRemnants.DistinctBy(r => r.Id)
                            .Where(r => r.Fate == PieceFate.Waste)
                            .Select(r => new
                            {
                                displayNumber = FormatRemnantDisplayNumber(r),
                                width = r.Width,
                                height = r.Height,
                                areaM2 = r.Area / 1_000_000.0
                            })
                    }
                },
                stockAfter = new
                {
                    usedFromStock = initialStock
                        .Where(r => result.AllTiles.Any(t => t.IsReused && t.SourceRemnantId == r.Id))
                        .Select(r => new
                        {
                            id = r.Id.ToString().Substring(0, 8),
                            displayNumber = FormatRemnantDisplayNumber(r),
                            width = r.Width,
                            height = r.Height
                        }),
                    createdSaved = result.CreatedRemnants
                        .Where(r => r.Source != "Создан и переиспользован")
                        .Select(r => new
                        {
                            id = r.Id.ToString().Substring(0, 8),
                            displayNumber = FormatRemnantDisplayNumber(r),
                            width = r.Width,
                            height = r.Height,
                            source = r.Source,
                            baseBlockNumber = r.BaseBlockNumber,
                            suffixCounter = r.SuffixCounter
                        }),
                    createdAndReused = result.CreatedRemnants
                        .Where(r => r.Source == "Создан и переиспользован")
                        .Select(r => new
                        {
                            id = r.Id.ToString().Substring(0, 8),
                            displayNumber = FormatRemnantDisplayNumber(r),
                            width = r.Width,
                            height = r.Height
                        })
                },
                warnings = result.Warnings,
                errors = result.Errors
            };

            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };
            File.WriteAllText(JsonPath, JsonConvert.SerializeObject(diag, settings), Encoding.UTF8);
        }

        // ──────────────────────────────────────────────────────────────
        //  Helpers
        // ──────────────────────────────────────────────────────────────

        private static void WriteSection(StringBuilder sb, string title)
        {
            sb.AppendLine($"───────────────────────────────────────────────────────────");
            sb.AppendLine($"  {title}");
            sb.AppendLine($"───────────────────────────────────────────────────────────");
        }

        private static void ValidateAndLog(StringBuilder sb, LayoutResult result, FacadeInput input, OrToolsSolution solution)
        {
            const double tol = 0.1;

            // --- Collect joints per row and min block width ---
            // FIX: Разделяем min block width для стандартных и оконных сегментов
            double minBlockWidthStd = double.MaxValue;  // глухие стены: порог MinBlock (200)
            double minBlockWidthWin = double.MaxValue;   // у окон: порог MinBlockNearWindow (150)
            var jointsByRow = new Dictionary<int, List<double>>();

            foreach (var row in solution.Rows)
            {
                var rowJoints = new List<double>();
                foreach (var seg in row.Segments)
                {
                    // Определяем, является ли сегмент околооконным
                    var inputSeg = input.Rows
                        .Where(r => r.RowIndex == row.RowIndex)
                        .SelectMany(r => r.Segments)
                        .FirstOrDefault(s => Math.Abs(s.StartX - seg.StartX) < 1.0);
                    bool isWindowSeg = inputSeg != null && (inputSeg.NearWindowLeft || inputSeg.NearWindowRight);

                    double x = seg.StartX;
                    foreach (var block in seg.Blocks)
                    {
                        if (isWindowSeg)
                        {
                            if (block.Width < minBlockWidthWin) minBlockWidthWin = block.Width;
                        }
                        else
                        {
                            if (block.Width < minBlockWidthStd) minBlockWidthStd = block.Width;
                        }
                        x += block.Width;
                        if (x < seg.EndX - 0.001)
                            rowJoints.Add(x);
                    }
                }
                jointsByRow[row.RowIndex] = rowJoints;
            }

            // C1: проверяем каждый контекст по своему порогу
            bool c1std = minBlockWidthStd >= input.Constraints.MinBlock - tol || minBlockWidthStd == double.MaxValue;
            bool c1win = minBlockWidthWin >= input.Constraints.MinBlockNearWindow - tol || minBlockWidthWin == double.MaxValue;
            bool c1 = c1std && c1win;
            double minBlockOverall = Math.Min(
                minBlockWidthStd == double.MaxValue ? 9999 : minBlockWidthStd,
                minBlockWidthWin == double.MaxValue ? 9999 : minBlockWidthWin);

            // --- C12: Stagger — min distance between joints in adjacent rows ---
            double minStagger = double.MaxValue;
            var sortedRows = solution.Rows.OrderBy(r => r.RowIndex).ToList();
            for (int ri = 1; ri < sortedRows.Count; ri++)
            {
                var prevJoints = jointsByRow.GetValueOrDefault(sortedRows[ri - 1].RowIndex);
                var currJoints = jointsByRow.GetValueOrDefault(sortedRows[ri].RowIndex);
                if (prevJoints == null || currJoints == null || prevJoints.Count == 0 || currJoints.Count == 0)
                    continue;

                foreach (double j in currJoints)
                {
                    foreach (double pj in prevJoints)
                    {
                        double dist = Math.Abs(j - pj);
                        if (dist < minStagger) minStagger = dist;
                    }
                }
            }
            bool c12 = minStagger >= input.Constraints.MinJointOffset - tol || minStagger == double.MaxValue;

            // --- C12a: CornerZone — no joint closer than 150mm to window overlap edge ---
            // FIX: Стыки на границах окон (segment splits) — это структурные разрывы,
            // а не нарушения CornerZone. Исключаем стыки, совпадающие с Min/MaxX окна.
            double minCornerDist = double.MaxValue;
            double cornerZone = input.Constraints.CornerZone;
            double overlap = input.Constraints.WindowOverlap;

            if (input.Windows.Count > 0)
            {
                var overlapEdges = new List<double>();
                var windowPhysicalEdges = new List<double>();
                foreach (var w in input.Windows)
                {
                    overlapEdges.Add(w.MinX + overlap);
                    overlapEdges.Add(w.MaxX - overlap);
                    windowPhysicalEdges.Add(w.MinX);
                    windowPhysicalEdges.Add(w.MaxX);
                    windowPhysicalEdges.Add(w.MinX + overlap); // effective edge
                    windowPhysicalEdges.Add(w.MaxX - overlap); // effective edge
                }

                foreach (var rowJoints in jointsByRow.Values)
                {
                    foreach (double j in rowJoints)
                    {
                        // Пропускаем стыки, совпадающие со структурными границами окна
                        bool isAtWindowEdge = windowPhysicalEdges.Any(e => Math.Abs(j - e) < 1.0);
                        if (isAtWindowEdge) continue;

                        foreach (double edge in overlapEdges)
                        {
                            double dist = Math.Abs(j - edge);
                            if (dist < minCornerDist) minCornerDist = dist;
                        }
                    }
                }
            }
            bool c12a = minCornerDist >= cornerZone - tol || minCornerDist == double.MaxValue;

            // --- C13: Consecutive remnants (with blind/window distinction) ---
            int maxConsecBlind = 0;
            int maxConsecWindow = 0;
            foreach (var row in solution.Rows)
            {
                foreach (var seg in row.Segments)
                {
                    var inputSeg = input.Rows
                        .Where(r => r.RowIndex == row.RowIndex)
                        .SelectMany(r => r.Segments)
                        .FirstOrDefault(s => Math.Abs(s.StartX - seg.StartX) < 1.0);
                    bool isWindowSeg = inputSeg != null && (inputSeg.NearWindowLeft || inputSeg.NearWindowRight);

                    int consec = 0;
                    foreach (var block in seg.Blocks)
                    {
                        if (block.Type == BlockType.Remnant || block.Type == BlockType.CutRemnant)
                        {
                            consec++;
                            if (isWindowSeg) { if (consec > maxConsecWindow) maxConsecWindow = consec; }
                            else { if (consec > maxConsecBlind) maxConsecBlind = consec; }
                        }
                        else consec = 0;
                    }
                }
            }
            int maxConsec = Math.Max(maxConsecBlind, maxConsecWindow);
            bool c13blind = maxConsecBlind <= input.Constraints.MaxConsecutiveRemnants;
            bool c13window = maxConsecWindow <= input.Constraints.MaxConsecutiveRemnantsWindow;
            bool c13 = c13blind && c13window;

            // --- Output ---
            sb.AppendLine($"  C1   MinBlock ≥{input.Constraints.MinBlock:F0}/{input.Constraints.MinBlockNearWindow:F0}:  " +
                          $"{(c1 ? "✅ PASS" : "❌ FAIL")} (глухая={minBlockWidthStd:F0}, окно={minBlockWidthWin:F0})");
            sb.AppendLine($"  C12  Stagger ≥{input.Constraints.MinJointOffset:F0}:       {(c12 ? "✅ PASS" : "❌ FAIL")} (мин={minStagger:F0})");
            sb.AppendLine($"  C12a CornerZone ≥{cornerZone:F0}:     {(c12a ? "✅ PASS" : "❌ FAIL")} (мин={minCornerDist:F0})");
            sb.AppendLine($"  C13  ConsecRemnants ≤{input.Constraints.MaxConsecutiveRemnants}/{input.Constraints.MaxConsecutiveRemnantsWindow}: " +
                          $"{(c13 ? "✅ PASS" : "❌ FAIL")} (глухая={maxConsecBlind}, окно={maxConsecWindow})");

            // --- CH: Row height ---
            double minH = input.Rows.Any() ? input.Rows.Min(r => r.Height) : 0;
            bool cH = minH >= input.Constraints.MinRowHeight - tol;
            sb.AppendLine($"  CH   MinRowHeight ≥{input.Constraints.MinRowHeight:F0}:   {(cH ? "✅ PASS" : "❌ FAIL")} (мин={minH:F0})");

            // Standard height preference
            int std600 = input.Rows.Count(r => Math.Abs(r.Height - input.Constraints.TileHeight) < tol);
            sb.AppendLine($"  CHstd Рядов с H={input.Constraints.TileHeight:F0}: {std600}/{input.Rows.Count}");
        }

        /// <summary>
        /// Форматирует номер остатка: X.Y.NNN.ZZ (если есть BaseBlockNumber).
        /// Fallback — короткий GUID (8 символов).
        /// </summary>
        private static string FormatRemnantDisplayNumber(Remnant r)
        {
            if (!string.IsNullOrEmpty(r.BaseBlockNumber))
            {
                return r.SuffixCounter > 0
                    ? $"{r.BaseBlockNumber}.{r.SuffixCounter:D2}"
                    : r.BaseBlockNumber;
            }
            return r.Id.ToString().Substring(0, 8);
        }
    }
}
