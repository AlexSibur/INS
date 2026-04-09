using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.Data
{
    /// <summary>
    /// Экспорт/импорт остатков в Excel-файл (.xlsx).
    /// Файл обновляется после каждого запуска раскладки.
    /// Путь по умолчанию: C:\DB_INS\remnants.xlsx
    /// </summary>
    public static class RemnantExcelExporter
    {
        private static readonly string ExcelPath = @"C:\DB_INS\remnants.xlsx";
        private static readonly string DbDir = @"C:\DB_INS";

        private const string MovementSheetName = "Движение остатков";

        /// <summary>
        /// Сохраняет остатки в Excel-файл.
        /// </summary>
        public static void SaveToExcel(List<Remnant> remnants, string? path = null)
        {
            string filePath = path ?? ExcelPath;

            try
            {
                if (!Directory.Exists(DbDir))
                    Directory.CreateDirectory(DbDir);

                using var workbook = new XLWorkbook();
                var ws = workbook.Worksheets.Add("Статистика");
                WriteCombinedStatisticsSheet(ws, remnants, null);

                workbook.SaveAs(filePath);
                LogOperation($"Excel сохранён: {remnants.Count} остатков → {filePath}");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка записи Excel: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Обновляет Excel с полной информацией о движении остатков.
        /// </summary>
        public static void UpdateWithMovement(
            List<Remnant> initialStock,
            List<Guid> usedFromStock,
            List<Remnant> createdRemnants,
            List<Remnant> allRemnants,
            LayoutStatisticsDTO? stats = null,
            List<string>? diagnostics = null,
            List<TileInfo>? allTiles = null)
        {
            LogOperation($"[FIX] UpdateWithMovement вызван: initialStock={initialStock.Count}, " +
                $"usedFromStock={usedFromStock.Count}, created={createdRemnants.Count}, " +
                $"allRemnants={allRemnants.Count}, stats={stats != null}, diag={diagnostics?.Count ?? 0}");
            try
            {
                foreach (var r in allRemnants)
                {
                    if (usedFromStock.Contains(r.Id))
                        r.IsUsed = true;
                }

                if (!Directory.Exists(DbDir))
                    Directory.CreateDirectory(DbDir);

                using var workbook = new XLWorkbook();

                var statsWs = workbook.Worksheets.Add("Статистика");
                WriteCombinedStatisticsSheet(statsWs, allRemnants, stats);

                var movementWs = workbook.Worksheets.Add(MovementSheetName);
                WriteMovementSheet(movementWs, initialStock, usedFromStock, createdRemnants);

                if (allTiles != null && allTiles.Any())
                {
                    var tileMovWs = workbook.Worksheets.Add("Сводная");
                    WriteTileMovementSheet(tileMovWs, allTiles, createdRemnants);
                }

                // Лист «Нач. склад» — шаблон для ввода начального склада пользователем
                var initStockWs = workbook.Worksheets.Add("Нач. склад");
                WriteInitialStockTemplateSheet(initStockWs, initialStock);

                // Лист «Диагностика» удалён — данные хранятся только в C:\DB_INS\diagnostics.txt

                // Попытка сохранения: если файл заблокирован — сохраняем с таймстампом
                try
                {
                    workbook.SaveAs(ExcelPath);
                    LogOperation($"Excel с движением сохранён: {allRemnants.Count} остатков");
                }
                catch (IOException)
                {
                    string fallback = Path.Combine(DbDir,
                        $"remnants_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                    workbook.SaveAs(fallback);
                    LogOperation($"Основной файл заблокирован, сохранён: {fallback}");
                    throw new IOException(
                        $"Файл remnants.xlsx заблокирован (открыт в Excel?). Отчёт сохранён: {fallback}");
                }
            }
            catch (IOException)
            {
                throw; // пробрасываем — PluginCommands покажет пользователю
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка записи Excel с движением: {ex}");
                throw;
            }
        }

        private static void WriteMovementSheet(
            IXLWorksheet ws,
            List<Remnant> initialStock,
            List<Guid> usedFromStockIds,
            List<Remnant> createdRemnants)
        {
            ws.Cell(1, 1).Value = "ДВИЖЕНИЕ ОСТАТКОВ";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(2, 1).Value = $"Дата раскладки: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

            int row = 4;

            // Сводка
            ws.Cell(row, 1).Value = "СВОДКА";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            row++;

            var usedSet = new HashSet<Guid>(usedFromStockIds);
            var uniqueCreated = createdRemnants.DistinctBy(r => r.Id).ToList();
            var remaining = uniqueCreated.Where(r => !r.IsUsed).ToList();
            var propagated = uniqueCreated.Where(r => r.IsUsed).ToList();

            WriteStatRow(ws, ref row, "Начальный склад", $"{initialStock.Count} шт. ({initialStock.Sum(r => r.Area) / 1_000_000.0:F3} м²)");
            WriteStatRow(ws, ref row, "Использовано из склада", $"{usedFromStockIds.Count} шт.");
            WriteStatRow(ws, ref row, "Создано при раскрое", $"{uniqueCreated.Count} шт. ({uniqueCreated.Sum(r => r.Area) / 1_000_000.0:F3} м²)");
            WriteStatRow(ws, ref row, "  → пропагация", $"{propagated.Count} шт.");
            WriteStatRow(ws, ref row, "  → на склад", $"{remaining.Count} шт.");

            row += 2;

            // Начальный склад
            ws.Cell(row, 1).Value = "НАЧАЛЬНЫЙ СКЛАД";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightBlue;
            row++;
            WriteMovementHeader(ws, row);
            row++;

            foreach (var r in initialStock.OrderByDescending(r => r.Area))
            {
                bool wasUsed = usedSet.Contains(r.Id);
                ws.Cell(row, 1).Value = FormatRemnantDisplayNumber(r);
                ws.Cell(row, 2).Value = r.Width;
                ws.Cell(row, 3).Value = r.Height;
                ws.Cell(row, 4).Value = r.Area / 1_000_000.0;
                ws.Cell(row, 5).Value = wasUsed ? "ИСПОЛЬЗОВАН" : "не использован";
                ws.Cell(row, 6).Value = r.Source ?? "";

                if (wasUsed)
                    ws.Range(row, 1, row, 7).Style.Fill.BackgroundColor = XLColor.LightGreen;
                else
                    ws.Range(row, 1, row, 7).Style.Fill.BackgroundColor = XLColor.LightYellow;
                row++;
            }

            row += 2;

            // Созданные остатки
            if (remaining.Any())
            {
                ws.Cell(row, 1).Value = $"СОЗДАННЫЕ ОСТАТКИ (на складе): {remaining.Count} шт.";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
                row++;
                WriteMovementHeader(ws, row);
                row++;

                foreach (var r in remaining.OrderByDescending(r => r.Area))
                {
                    ws.Cell(row, 1).Value = FormatRemnantDisplayNumber(r);
                    ws.Cell(row, 2).Value = r.Width;
                    ws.Cell(row, 3).Value = r.Height;
                    ws.Cell(row, 4).Value = r.Area / 1_000_000.0;
                    ws.Cell(row, 5).Value = "на складе";
                    ws.Cell(row, 6).Value = r.Source ?? "";

                    if (!string.IsNullOrEmpty(r.CutHistory))
                        ws.Cell(row, 7).Value = r.CutHistory;

                    row++;
                }
            }

            ws.Column(2).Style.NumberFormat.Format = "#,##0";
            ws.Column(3).Style.NumberFormat.Format = "#,##0";
            ws.Column(4).Style.NumberFormat.Format = "0.000";
            ws.Columns().AdjustToContents();
        }

        private static void WriteMovementHeader(IXLWorksheet ws, int row)
        {
            ws.Cell(row, 1).Value = "Номер (X.Y.NNN.ZZ)";
            ws.Cell(row, 2).Value = "Ширина (мм)";
            ws.Cell(row, 3).Value = "Высота (мм)";
            ws.Cell(row, 4).Value = "Площадь (м²)";
            ws.Cell(row, 5).Value = "Статус";
            ws.Cell(row, 6).Value = "Источник";
            ws.Cell(row, 7).Value = "История нарезки";
            ws.Range(row, 1, row, 7).Style.Font.Bold = true;
            ws.Range(row, 1, row, 7).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
        }

        private static void WriteDiagnosticsSheet(IXLWorksheet ws, List<string> diagnostics)
        {
            ws.Cell(1, 1).Value = "ДИАГНОСТИКА";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.FontColor = XLColor.DarkRed;

            int row = 3;
            var grouped = diagnostics
                .GroupBy(d => d.Split(':')[0])
                .Select(g => new { Code = g.Key, Count = g.Count(), Examples = g.Take(3).ToList() })
                .OrderByDescending(g => g.Count);

            foreach (var g in grouped)
            {
                ws.Cell(row, 1).Value = $"{g.Code} ({g.Count} шт.)";
                ws.Cell(row, 1).Style.Font.Bold = true;
                row++;
                foreach (var ex in g.Examples)
                {
                    ws.Cell(row, 1).Value = "  - " + ex;
                    row++;
                }
                if (g.Count > 3)
                {
                    ws.Cell(row, 1).Value = $"  ... и еще {g.Count - 3} шт.";
                    row++;
                }
                row++;
            }
            ws.Column(1).Width = 100;
            ws.Column(1).Style.Alignment.WrapText = true;
        }

        private static void WriteCombinedStatisticsSheet(
            IXLWorksheet ws,
            List<Remnant> remnants,
            LayoutStatisticsDTO? stats)
        {
            ws.Cell(1, 1).Value = "СТАТИСТИКА";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 16;
            ws.Cell(2, 1).Value = $"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

            ws.Column(1).Width = 40;
            ws.Column(2).Width = 25;

            int row = 4;

            // --- Секция А: Сводка склада ---
            ws.Cell(row, 1).Value = "А. Сводка склада";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
            row++;

            var available = remnants.Where(r => !r.IsUsed).ToList();
            var used = remnants.Where(r => r.IsUsed).ToList();

            WriteStatRow(ws, ref row, "Всего остатков", remnants.Count.ToString());
            WriteStatRow(ws, ref row, "Доступных", available.Count.ToString());
            WriteStatRow(ws, ref row, "Использованных", used.Count.ToString());
            WriteStatRow(ws, ref row, "Площадь (доступные)", $"{available.Sum(r => r.Area) / 1_000_000.0:F3} м²");
            row++;

            if (stats != null)
            {
                // --- Секция Б: Основная статистика ---
                ws.Cell(row, 1).Value = "Б. Основная статистика раскладки";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
                row++;

                if (!string.IsNullOrEmpty(stats.SolverStatus))
                    WriteStatRow(ws, ref row, "Статус OR-Tools", stats.SolverStatus);
                if (stats.SolveTimeMs > 0)
                    WriteStatRow(ws, ref row, "Время решения (мс)", stats.SolveTimeMs.ToString());
                row++;

                double areaNetto = stats.NetInsulationArea;
                double areaPurchased = stats.TotalNewMaterialArea;
                double areaStock = stats.CreatedRemnantsArea;
                double areaWaste = stats.WasteArea;
                double excessPct = areaNetto > 0 ? (areaPurchased / areaNetto - 1.0) * 100.0 : 0;

                WriteStatRow(ws, ref row, "Площадь утепления (нетто)", $"{areaNetto:F3} м²");
                WriteStatRow(ws, ref row, "Закупка нового материала", $"{areaPurchased:F3} м²");
                WriteStatRow(ws, ref row, "  Остатки на складе", $"{areaStock:F3} м²");
                WriteStatRow(ws, ref row, "  Отходы", $"{areaWaste:F3} м²");

                ws.Cell(row, 1).Value = "Перерасход";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = $"{excessPct:F1}%";
                ws.Cell(row, 2).Style.Font.Bold = true;
                ws.Cell(row, 2).Style.Fill.BackgroundColor = excessPct > 7.0 ? XLColor.Red : XLColor.LightGreen;
                row++;
                row++;

                WriteStatRow(ws, ref row, "Новых плит", $"{stats.NewTilesCount} шт.");
                WriteStatRow(ws, ref row, "Использовано остатков", $"{stats.ReusedRemnantsCount} шт.");
                WriteStatRow(ws, ref row, "Создано остатков", $"{stats.CreatedRemnantsCount} шт. (на склад: {stats.CreatedRemnantsToStockCount}, переисп.: {stats.CreatedRemnantsReusedCount})");
                row++;

                // --- Секция В: Материальный баланс ---
                ws.Cell(row, 1).Value = "В. Материальный баланс";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
                row++;

                ws.Cell(row, 1).Value = "Геометрия фасада"; ws.Cell(row, 1).Style.Font.Bold = true; row++;
                WriteStatRow(ws, ref row, "Площадь фасада (брутто)", $"{stats.FacadeGrossArea:F3} м²");
                WriteStatRow(ws, ref row, "Площадь окон", $"{stats.WindowsArea:F3} м²");
                WriteStatRow(ws, ref row, "Площадь утепления (нетто)", $"{stats.NetInsulationArea:F3} м²");
                WriteStatRow(ws, ref row, "Количество окон", $"{stats.WindowsCount} шт.");
                row++;

                ws.Cell(row, 1).Value = "Новый материал"; ws.Cell(row, 1).Style.Font.Bold = true; row++;
                WriteStatRow(ws, ref row, "Количество новых плит", $"{stats.NewTilesCount} шт.");
                WriteStatRow(ws, ref row, "Общая площадь нового материала", $"{stats.TotalNewMaterialArea:F3} м²");
                WriteStatRow(ws, ref row, "Площадь новых плит (на фасаде)", $"{stats.NewTilesArea:F3} м²");
                WriteStatRow(ws, ref row, "Площадь созданных остатков", $"{stats.CreatedRemnantsArea:F3} м²");
                WriteStatRow(ws, ref row, "Отходы от новых плит", $"{stats.WasteFromNewTilesArea:F3} м²");
                row++;

                ws.Cell(row, 1).Value = "Переиспользование"; ws.Cell(row, 1).Style.Font.Bold = true; row++;
                WriteStatRow(ws, ref row, "Количество переисп. остатков", $"{stats.ReusedRemnantsCount} шт.");
                WriteStatRow(ws, ref row, "Площадь переисп. остатков", $"{stats.ReusedArea:F3} м²");
                WriteStatRow(ws, ref row, "Отходы от переисп. остатков", $"{stats.WasteFromRemnantsArea:F3} м²");
                row++;

                ws.Cell(row, 1).Value = "Ключевые метрики"; ws.Cell(row, 1).Style.Font.Bold = true; row++;

                // Перерасход (консервативный): TotalNew / NetArea - 1
                ws.Cell(row, 1).Value = "Перерасход материала (%)";
                ws.Cell(row, 2).Value = $"{stats.OverconsumptionPct:F1}%";
                ws.Cell(row, 2).Style.Font.Bold = true;
                ws.Cell(row, 2).Style.Fill.BackgroundColor = stats.OverconsumptionPct > 7.0 ? XLColor.Red : XLColor.LightGreen;
                row++;

                // Скорректированный перерасход (с учётом остатков на складе):
                // (TotalNew - StockRemnants) / NetArea - 1. Учитывает, что часть нового материала
                // осталась на складе и будет использована в следующих проектах.
                double stockArea = available.Sum(r => r.Area) / 1_000_000.0;
                double effectivePct = stats.NetInsulationArea > 0
                    ? ((stats.TotalNewMaterialArea - stockArea) / stats.NetInsulationArea * 100.0 - 100.0) : 0;
                ws.Cell(row, 1).Value = "Перерасход с учётом склада (%)";
                ws.Cell(row, 1).Style.Font.Italic = true;
                ws.Cell(row, 2).Value = $"{effectivePct:F1}%";
                ws.Cell(row, 2).Style.Font.Italic = true;
                ws.Cell(row, 2).Style.Fill.BackgroundColor = effectivePct > 7.0 ? XLColor.Red : XLColor.LightGreen;
                row++;

                WriteStatRow(ws, ref row, "КПД раскроя (%)", $"{stats.CuttingEfficiencyPct:F1}%");
                WriteStatRow(ws, ref row, "Экономия от склада (%)", $"{stats.StockSavingsPct:F1}%");
                WriteStatRow(ws, ref row, "Коэффициент использования", $"{stats.UtilizationRatio:P1}");
                row++;

                // --- Секция Г: Проверка перерасхода ---
                ws.Cell(row, 1).Value = "Г. Проверка перерасхода";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
                row++;

                WriteStatRow(ws, ref row, "(1) Площадь утепления (нетто) — базис 100%", $"{stats.NetInsulationArea:F3} м²");
                WriteStatRow(ws, ref row, "(2) Новые плиты на фасаде", $"{stats.NewTilesArea:F3} м²");
                WriteStatRow(ws, ref row, "(3) Остатки на складе", $"{stats.CreatedRemnantsArea:F3} м²");
                WriteStatRow(ws, ref row, "(4) Отходы от новых плит", $"{stats.WasteFromNewTilesArea:F3} м²");

                double totalNewVerify = stats.NewTilesArea + stats.CreatedRemnantsArea + stats.WasteFromNewTilesArea;
                WriteStatRow(ws, ref row, "(5) ИТОГО новый материал = (2)+(3)+(4)", $"{totalNewVerify:F3} м²");

                double gap = Math.Abs(stats.TotalNewMaterialArea - totalNewVerify);
                WriteStatRow(ws, ref row, "(6) Контроль: (5) vs. НовыеПлиты×ПлощадьПлиты",
                    gap > 0.01 ? $"расхождение = {gap:F4} м²" : "OK");

                double verifyPct = stats.NetInsulationArea > 0
                    ? (totalNewVerify / stats.NetInsulationArea * 100.0 - 100.0) : 0;
                ws.Cell(row, 1).Value = "(7) Перерасход = (5)/(1) − 1";
                ws.Cell(row, 2).Value = $"{verifyPct:F1}%";
                ws.Cell(row, 2).Style.Font.Bold = true;
                ws.Cell(row, 2).Style.Fill.BackgroundColor = verifyPct > 7.0 ? XLColor.Red : XLColor.LightGreen;
                row++;
            }

            ws.Columns().AdjustToContents();
        }

        private static void WriteStatRow(IXLWorksheet ws, ref int row, string name, string value)
        {
            ws.Cell(row, 1).Value = name;
            ws.Cell(row, 2).Value = value;
            row++;
        }

        /// <summary>
        /// Formats remnant display number: X.Y.NNN.ZZ if BaseBlockNumber present, else short GUID.
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

        /// <summary>
        /// Лист "Сводная" — полный список плит на фасаде + остатки (tile-by-tile traceability).
        /// </summary>
        private static void WriteTileMovementSheet(
            IXLWorksheet ws,
            List<TileInfo> allTiles,
            List<Remnant> createdRemnants,
            double tileWidth = 1200, double tileHeight = 600, double minRemnantToSave = 150)
        {
            ws.Cell(1, 1).Value = "СВОДНАЯ: ДВИЖЕНИЕ МАТЕРИАЛОВ";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 16;
            ws.Cell(2, 1).Value = $"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

            // --- Section A: Tiles on facade ---
            int row = 4;
            ws.Cell(row, 1).Value = "А. ПЛИТЫ НА ФАСАДЕ";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
            row++;

            // Header (колонки «Сегмент» и «Позиция X» удалены — данные только в diagnostics.txt)
            ws.Cell(row, 1).Value = "Номер плиты";
            ws.Cell(row, 2).Value = "Ряд";
            ws.Cell(row, 3).Value = "Размер (ШxВ)";
            ws.Cell(row, 4).Value = "Площадь (м²)";
            ws.Cell(row, 5).Value = "Тип";
            ws.Cell(row, 6).Value = "Источник";
            ws.Cell(row, 7).Value = "Остаток";
            ws.Cell(row, 8).Value = "Размер остатка";
            ws.Range(row, 1, row, 8).Style.Font.Bold = true;
            ws.Range(row, 1, row, 8).Style.Fill.BackgroundColor = XLColor.PaleTurquoise;
            row++;

            double SafePositionX(TileInfo t)
            {
                try { return t.Position.X; }
                catch { return 0; }
            }

            // Build lookup: tile BlockNumber -> remnants created from it
            var remnantsByParent = new Dictionary<string, List<Remnant>>();
            var uniqueCreatedAll = createdRemnants.DistinctBy(r => r.Id).ToList();
            foreach (var r in uniqueCreatedAll)
            {
                if (string.IsNullOrEmpty(r.BaseBlockNumber)) continue;
                string parentKey = r.SuffixCounter <= 1
                    ? r.BaseBlockNumber
                    : $"{r.BaseBlockNumber}.{(r.SuffixCounter - 1):D2}";
                if (!remnantsByParent.ContainsKey(parentKey))
                    remnantsByParent[parentKey] = new List<Remnant>();
                remnantsByParent[parentKey].Add(r);
            }

            foreach (var tile in allTiles.OrderBy(t => t.RowIndex).ThenBy(t => SafePositionX(t)))
            {
                ws.Cell(row, 1).Value = tile.BlockNumber;
                ws.Cell(row, 2).Value = tile.RowIndex + 1;
                ws.Cell(row, 3).Value = $"{tile.Width:F0}x{tile.Height:F0}";
                ws.Cell(row, 4).Value = tile.Width * tile.Height / 1_000_000.0;
                ws.Cell(row, 5).Value = tile.IsReused ? "Остаток" : "Новая";
                ws.Cell(row, 6).Value = tile.ParentInfo;

                // Вычисляем остаток от раскроя исходного материала
                double sourceW = tile.IsReused && tile.SourceRemnantWidth > 0
                    ? tile.SourceRemnantWidth : tileWidth;
                double sourceH = tile.IsReused && tile.SourceRemnantHeight > 0
                    ? tile.SourceRemnantHeight : tileHeight;
                double widthLeftover = sourceW - tile.Width;
                double heightLeftover = sourceH - tile.Height;
                if (widthLeftover < 0.5) widthLeftover = 0;
                if (heightLeftover < 0.5) heightLeftover = 0;

                if (remnantsByParent.TryGetValue(tile.BlockNumber, out var childRemnants))
                {
                    var names = childRemnants.Select(r => FormatRemnantDisplayNumber(r));
                    var sizes = childRemnants.Select(r => $"{r.Width:F0}x{r.Height:F0}");
                    ws.Cell(row, 7).Value = string.Join("; ", names);
                    ws.Cell(row, 8).Value = string.Join("; ", sizes);
                }
                else if (widthLeftover > 0 || heightLeftover > 0)
                {
                    double wasteW = widthLeftover > 0 ? widthLeftover : tile.Width;
                    double wasteH = heightLeftover > 0 ? heightLeftover : tile.Height;
                    if (widthLeftover > 0 && heightLeftover > 0)
                    {
                        wasteW = widthLeftover;
                        wasteH = sourceH;
                    }

                    // Правило маркировки:
                    // - неликвид (< minRemnantToSave) => "отход"
                    // - ликвидный остаток => номер (пишется только через childRemnants выше)
                    if (wasteW < minRemnantToSave || wasteH < minRemnantToSave)
                    {
                        ws.Cell(row, 7).Value = "отход";
                        ws.Cell(row, 8).Value = $"{wasteW:F0}x{wasteH:F0}";
                        ws.Cell(row, 7).Style.Font.FontColor = XLColor.Red;
                        ws.Cell(row, 8).Style.Font.FontColor = XLColor.Red;
                    }
                    else
                    {
                        // Промежуточные виртуальные переносы внутри ряда не маркируем как отход.
                        ws.Cell(row, 7).Value = string.Empty;
                        ws.Cell(row, 8).Value = string.Empty;
                    }
                }

                if (tile.IsReused)
                    ws.Range(row, 1, row, 8).Style.Fill.BackgroundColor = XLColor.LightGreen;
                row++;
            }

            row += 2;

            // --- Section B: Created remnants (to stock / waste) ---
            var uniqueCreated = createdRemnants.DistinctBy(r => r.Id).ToList();
            var toStock = uniqueCreated.Where(r =>
                r.Source != "Создан и переиспользован" && r.Fate != PieceFate.Waste).ToList();
            var waste = uniqueCreated.Where(r => r.Fate == PieceFate.Waste).ToList();
            var reusedIntra = uniqueCreated.Where(r => r.Source == "Создан и переиспользован").ToList();

            ws.Cell(row, 1).Value = "Б. СОЗДАННЫЕ ОСТАТКИ (НА СКЛАД)";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
            row++;

            ws.Cell(row, 1).Value = "Номер";
            ws.Cell(row, 2).Value = "Ширина (мм)";
            ws.Cell(row, 3).Value = "Высота (мм)";
            ws.Cell(row, 4).Value = "Площадь (м²)";
            ws.Cell(row, 5).Value = "Источник";
            ws.Cell(row, 6).Value = "История нарезки";
            ws.Range(row, 1, row, 6).Style.Font.Bold = true;
            ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.PaleTurquoise;
            row++;

            foreach (var r in toStock.OrderByDescending(r => r.Area))
            {
                ws.Cell(row, 1).Value = FormatRemnantDisplayNumber(r);
                ws.Cell(row, 2).Value = r.Width;
                ws.Cell(row, 3).Value = r.Height;
                ws.Cell(row, 4).Value = r.Area / 1_000_000.0;
                ws.Cell(row, 5).Value = r.Source;
                ws.Cell(row, 6).Value = r.CutHistory ?? "";
                row++;
            }

            row += 2;

            // --- Section C: Reused within facade ---
            if (reusedIntra.Any())
            {
                ws.Cell(row, 1).Value = "В. ПЕРЕИСПОЛЬЗОВАНЫ В РАСКЛАДКЕ (не на склад)";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightYellow;
                row++;

                ws.Cell(row, 1).Value = "Номер";
                ws.Cell(row, 2).Value = "Ширина (мм)";
                ws.Cell(row, 3).Value = "Высота (мм)";
                ws.Cell(row, 4).Value = "Площадь (м²)";
                ws.Cell(row, 5).Value = "Источник";
                ws.Range(row, 1, row, 5).Style.Font.Bold = true;
                ws.Range(row, 1, row, 5).Style.Fill.BackgroundColor = XLColor.PaleTurquoise;
                row++;

                foreach (var r in reusedIntra.OrderByDescending(r => r.Area))
                {
                    ws.Cell(row, 1).Value = FormatRemnantDisplayNumber(r);
                    ws.Cell(row, 2).Value = r.Width;
                    ws.Cell(row, 3).Value = r.Height;
                    ws.Cell(row, 4).Value = r.Area / 1_000_000.0;
                    ws.Cell(row, 5).Value = r.Source;
                    ws.Range(row, 1, row, 5).Style.Fill.BackgroundColor = XLColor.LightYellow;
                    row++;
                }
                row += 2;
            }

            // --- Section D: Waste ---
            if (waste.Any())
            {
                ws.Cell(row, 1).Value = "Г. ОТХОДЫ (< минимума)";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightCoral;
                row++;

                ws.Cell(row, 1).Value = "Номер";
                ws.Cell(row, 2).Value = "Ширина (мм)";
                ws.Cell(row, 3).Value = "Высота (мм)";
                ws.Cell(row, 4).Value = "Площадь (м²)";
                ws.Range(row, 1, row, 4).Style.Font.Bold = true;
                ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = XLColor.PaleTurquoise;
                row++;

                foreach (var r in waste.OrderByDescending(r => r.Area))
                {
                    ws.Cell(row, 1).Value = FormatRemnantDisplayNumber(r);
                    ws.Cell(row, 2).Value = r.Width;
                    ws.Cell(row, 3).Value = r.Height;
                    ws.Cell(row, 4).Value = r.Area / 1_000_000.0;
                    ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = XLColor.MistyRose;
                    row++;
                }
            }

            ws.Column(4).Style.NumberFormat.Format = "0.000";
            ws.Column(6).Style.NumberFormat.Format = "0.000";
            ws.Columns().AdjustToContents();
        }

        /// <summary>
        /// Создаёт лист «Нач. склад» — шаблон для ввода начального склада.
        /// Пользователь заполняет таблицу и запускает INS_STOCK_IMPORT.
        /// </summary>
        private static void WriteInitialStockTemplateSheet(IXLWorksheet ws, List<Remnant> currentInitialStock)
        {
            int row = 1;

            ws.Cell(row, 1).Value = "НАЧАЛЬНЫЙ СКЛАД — заполните таблицу и запустите INS_STOCK_IMPORT";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.DarkBlue;
            ws.Range(row, 1, row, 3).Merge();
            row++;

            ws.Cell(row, 1).Value = "Команда INS_STOCK_IMPORT заменит текущий склад данными из этой таблицы.";
            ws.Cell(row, 1).Style.Font.Italic = true;
            ws.Cell(row, 1).Style.Font.FontColor = XLColor.DarkGray;
            ws.Range(row, 1, row, 3).Merge();
            row++;

            row++; // пустая строка

            ws.Cell(row, 1).Value = "Ширина (мм)";
            ws.Cell(row, 2).Value = "Высота (мм)";
            ws.Cell(row, 3).Value = "Количество (шт.)";
            ws.Range(row, 1, row, 3).Style.Font.Bold = true;
            ws.Range(row, 1, row, 3).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
            ws.Range(row, 1, row, 3).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            int headerRow = row;
            row++;

            if (currentInitialStock.Any())
            {
                ws.Cell(headerRow - 1, 1).Value =
                    $"Текущий склад ({currentInitialStock.Count} шт.) — отредактируйте или замените:";
                ws.Cell(headerRow - 1, 1).Style.Font.Italic = true;
                ws.Range(headerRow - 1, 1, headerRow - 1, 3).Merge();

                foreach (var r in currentInitialStock.OrderByDescending(x => x.Area))
                {
                    ws.Cell(row, 1).Value = (int)r.Width;
                    ws.Cell(row, 2).Value = (int)r.Height;
                    ws.Cell(row, 3).Value = 1;
                    ws.Range(row, 1, row, 3).Style.Fill.BackgroundColor = XLColor.LightYellow;
                    row++;
                }
            }

            int emptyRows = currentInitialStock.Any() ? 10 : 50;
            for (int i = 0; i < emptyRows; i++)
            {
                ws.Range(row, 1, row, 3).Style.Fill.BackgroundColor = XLColor.LightYellow;
                ws.Range(row, 1, row, 3).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                row++;
            }

            ws.Range(headerRow + 1, 1, row - 1, 2).Style.NumberFormat.Format = "0";
            ws.Range(headerRow + 1, 3, row - 1, 3).Style.NumberFormat.Format = "0";

            ws.Column(1).Width = 18;
            ws.Column(2).Width = 18;
            ws.Column(3).Width = 22;
        }

        private static void LogOperation(string message)
        {
            try
            {
                if (!Directory.Exists(DbDir))
                    Directory.CreateDirectory(DbDir);

                string logFile = Path.Combine(DbDir, "database.log");
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Excel] {message}";
                File.AppendAllText(logFile, logEntry + Environment.NewLine);
            }
            catch { }
        }
    }
}
