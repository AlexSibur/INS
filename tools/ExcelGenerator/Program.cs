// ExcelGenerator — standalone console app for generating remnants.xlsx
// Runs OUTSIDE AutoCAD to bypass assembly loading issues.
// Usage: ExcelGenerator.exe <input_json_path>
// Input JSON format: { InitialStock, UsedFromStockIds, CreatedRemnants, AllRemnants, Stats, Diagnostics }

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Newtonsoft.Json;

namespace ExcelGenerator
{
    // Mirror models from InsulationMasterPro.Models (simplified, no AutoCAD deps)
    public class RemnantDto
    {
        public Guid Id { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Area => Width * Height;
        public DateTime AddedDate { get; set; }
        public bool IsUsed { get; set; }
        public string Source { get; set; } = "";
        public string ShortId => Id.ToString("N").Substring(0, 4).ToUpper();
        public Guid? ParentRemnantId { get; set; }
        public string BaseBlockNumber { get; set; } = "";
        public int SuffixCounter { get; set; }
        public string CutHistory { get; set; } = "";
        public Guid? SourceBlockId { get; set; }
    }

    public class StatsDto
    {
        public string SolverStatus { get; set; } = "";
        public long SolveTimeMs { get; set; }
        public double NewTilesArea { get; set; }
        public int NewTilesCount { get; set; }
        public double ReusedArea { get; set; }
        public int ReusedRemnantsCount { get; set; }
        public double TotalInsulationArea { get; set; }
        public double WasteFromNewTilesArea { get; set; }
        public double WasteFromRemnantsArea { get; set; }
        public double WasteArea { get; set; }
        public double UtilizationRatio { get; set; }
        public double FacadeGrossArea { get; set; }
        public double WindowsArea { get; set; }
        public int WindowsCount { get; set; }
        public double NetInsulationArea { get; set; }
        public double TotalNewMaterialArea { get; set; }
        public double CreatedRemnantsArea { get; set; }
        public double OverconsumptionPct { get; set; }
        public bool OverConsumptionExceeded { get; set; }
        public double CuttingEfficiencyPct { get; set; }
        public double StockSavingsPct { get; set; }
        public int WasteDisposalCount { get; set; }
        public double WasteDisposalAreaM2 { get; set; }
        public int CreatedRemnantsCount { get; set; }
        public int CreatedRemnantsToStockCount { get; set; }
        public int CreatedRemnantsReusedCount { get; set; }
    }

    public class TileDto
    {
        public string BlockNumber { get; set; } = "";
        public int RowIndex { get; set; }
        public int SegmentIndex { get; set; }
        public double PositionX { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsReused { get; set; }
        public string ParentInfo { get; set; } = "";
        public double SourceRemnantWidth { get; set; }
        public double SourceRemnantHeight { get; set; }
    }

    public class ExcelInputDto
    {
        public List<RemnantDto> InitialStock { get; set; } = new();
        public List<Guid> UsedFromStockIds { get; set; } = new();
        public List<RemnantDto> CreatedRemnants { get; set; } = new();
        public List<RemnantDto> AllRemnants { get; set; } = new();
        public StatsDto? Stats { get; set; }
        public List<string>? Diagnostics { get; set; }
        public List<TileDto>? AllTiles { get; set; }
    }

    class Program
    {
        private static readonly string ExcelPath = @"C:\DB_INS\remnants.xlsx";
        private static readonly string LogPath = @"C:\DB_INS\database.log";

        static int Main(string[] args)
        {
            try
            {
                if (args.Length < 1)
                {
                    Console.Error.WriteLine("Usage: ExcelGenerator.exe <input_json_path>");
                    return 1;
                }

                string inputPath = args[0];
                if (!File.Exists(inputPath))
                {
                    Log($"[ExcelGen] Файл не найден: {inputPath}");
                    return 1;
                }

                string json = File.ReadAllText(inputPath);
                var input = JsonConvert.DeserializeObject<ExcelInputDto>(json);
                if (input == null)
                {
                    Log("[ExcelGen] Ошибка десериализации JSON");
                    return 1;
                }

                Log($"[ExcelGen] Запуск: initialStock={input.InitialStock.Count}, " +
                    $"used={input.UsedFromStockIds.Count}, created={input.CreatedRemnants.Count}, " +
                    $"all={input.AllRemnants.Count}");

                // Mark used remnants
                var usedSet = new HashSet<Guid>(input.UsedFromStockIds);
                foreach (var r in input.AllRemnants)
                {
                    if (usedSet.Contains(r.Id))
                        r.IsUsed = true;
                }

                using var workbook = new XLWorkbook();

                // Sheet 1: Статистика (объединённый: Сводка + Статистика + Свод)
                var statsWs = workbook.Worksheets.Add("Статистика");
                WriteCombinedStatisticsSheet(statsWs, input.AllRemnants, input.Stats);

                // Sheet 2: Движение остатков
                var movementWs = workbook.Worksheets.Add("Движение остатков");
                WriteMovementSheet(movementWs, input.InitialStock, input.UsedFromStockIds, input.CreatedRemnants);

                // Sheet 6: Сводная (tile-by-tile)
                if (input.AllTiles != null && input.AllTiles.Any())
                {
                    var tileWs = workbook.Worksheets.Add("Сводная");
                    WriteTileMovementSheet(tileWs, input.AllTiles, input.CreatedRemnants);
                }

                // Sheet 7: Диагностика (если есть)
                if (input.Diagnostics != null && input.Diagnostics.Any())
                {
                    var diagWs = workbook.Worksheets.Add("Диагностика");
                    WriteDiagnosticsSheet(diagWs, input.Diagnostics);
                }

                // Save with fallback
                try
                {
                    workbook.SaveAs(ExcelPath);
                    Log($"[ExcelGen] Excel сохранён: {input.AllRemnants.Count} остатков → {ExcelPath}");
                }
                catch (IOException)
                {
                    string fallback = Path.Combine(@"C:\DB_INS",
                        $"remnants_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
                    workbook.SaveAs(fallback);
                    Log($"[ExcelGen] Файл заблокирован, сохранён: {fallback}");
                }

                Console.WriteLine("OK");
                return 0;
            }
            catch (Exception ex)
            {
                Log($"[ExcelGen] ОШИБКА: {ex}");
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        // ========= Sheet writers (mirror of RemnantExcelExporter logic) =========

        static void WriteCombinedStatisticsSheet(IXLWorksheet ws, List<RemnantDto> remnants, StatsDto? stats)
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

                ws.Cell(row, 1).Value = "Перерасход материала (%)";
                ws.Cell(row, 2).Value = $"{stats.OverconsumptionPct:F1}%";
                ws.Cell(row, 2).Style.Font.Bold = true;
                ws.Cell(row, 2).Style.Fill.BackgroundColor = stats.OverconsumptionPct > 7.0 ? XLColor.Red : XLColor.LightGreen;
                row++;

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

                WriteStatRow(ws, ref row, "(1) Площадь утепления (нетто) - базис 100%", $"{stats.NetInsulationArea:F3} м²");
                WriteStatRow(ws, ref row, "(2) Новые плиты на фасаде", $"{stats.NewTilesArea:F3} м²");
                WriteStatRow(ws, ref row, "(3) Остатки на складе", $"{stats.CreatedRemnantsArea:F3} м²");
                WriteStatRow(ws, ref row, "(4) Отходы от новых плит", $"{stats.WasteFromNewTilesArea:F3} м²");

                double totalNewVerify = stats.NewTilesArea + stats.CreatedRemnantsArea + stats.WasteFromNewTilesArea;
                WriteStatRow(ws, ref row, "(5) ИТОГО новый материал = (2)+(3)+(4)", $"{totalNewVerify:F3} м²");

                double gap = Math.Abs(stats.TotalNewMaterialArea - totalNewVerify);
                WriteStatRow(ws, ref row, "(6) Контроль: (5) vs. НовыеПлиты x ПлощадьПлиты",
                    gap > 0.01 ? $"расхождение = {gap:F4} м²" : "OK");

                double verifyPct = stats.NetInsulationArea > 0
                    ? (totalNewVerify / stats.NetInsulationArea * 100.0 - 100.0) : 0;
                ws.Cell(row, 1).Value = "(7) Перерасход = (5)/(1) - 1";
                ws.Cell(row, 2).Value = $"{verifyPct:F1}%";
                ws.Cell(row, 2).Style.Font.Bold = true;
                ws.Cell(row, 2).Style.Fill.BackgroundColor = verifyPct > 7.0 ? XLColor.Red : XLColor.LightGreen;
                row++;
            }

            ws.Columns().AdjustToContents();
        }

        static void WriteMovementSheet(IXLWorksheet ws, List<RemnantDto> initialStock,
            List<Guid> usedFromStockIds, List<RemnantDto> createdRemnants)
        {
            ws.Cell(1, 1).Value = "ДВИЖЕНИЕ ОСТАТКОВ";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(2, 1).Value = $"Дата раскладки: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

            int row = 4;
            var usedSet = new HashSet<Guid>(usedFromStockIds);
            var remaining = createdRemnants.Where(r => !r.IsUsed).ToList();
            var propagated = createdRemnants.Where(r => r.IsUsed).ToList();

            // Summary
            ws.Cell(row, 1).Value = "СВОДКА";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            row++;

            WriteStatRow(ws, ref row, "Начальный склад", $"{initialStock.Count} шт. ({initialStock.Sum(r => r.Area) / 1_000_000.0:F3} м²)");
            WriteStatRow(ws, ref row, "Использовано из склада", $"{usedFromStockIds.Count} шт.");
            WriteStatRow(ws, ref row, "Создано при раскрое", $"{createdRemnants.Count} шт. ({createdRemnants.Sum(r => r.Area) / 1_000_000.0:F3} м²)");
            WriteStatRow(ws, ref row, "  → пропагация", $"{propagated.Count} шт.");
            WriteStatRow(ws, ref row, "  → на склад", $"{remaining.Count} шт.");

            row += 2;

            // Initial stock
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
                    ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.LightGreen;
                else
                    ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.LightYellow;
                row++;
            }

            row += 2;

            // Created remnants
            if (remaining.Any())
            {
                ws.Cell(row, 1).Value = "СОЗДАННЫЕ ОСТАТКИ (→ склад)";
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
                    ws.Cell(row, 5).Value = "НОВЫЙ";
                    ws.Cell(row, 6).Value = r.Source ?? "";
                    ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor = XLColor.LightGreen;
                    row++;
                }
            }

            ws.Columns().AdjustToContents();
        }

        static void WriteDiagnosticsSheet(IXLWorksheet ws, List<string> diagnostics)
        {
            ws.Cell(1, 1).Value = "ДИАГНОСТИКА";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;

            int row = 3;
            foreach (var d in diagnostics)
            {
                ws.Cell(row, 1).Value = d;
                if (d.StartsWith("E_"))
                    ws.Cell(row, 1).Style.Font.FontColor = XLColor.Red;
                row++;
            }

            ws.Columns().AdjustToContents();
        }

        static void WriteTileMovementSheet(IXLWorksheet ws, List<TileDto> allTiles, List<RemnantDto> createdRemnants,
            double tileWidth = 1200, double tileHeight = 600, double minRemnantToSave = 150)
        {
            ws.Cell(1, 1).Value = "СВОДНАЯ: ДВИЖЕНИЕ МАТЕРИАЛОВ";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 16;
            ws.Cell(2, 1).Value = $"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

            int row = 4;
            ws.Cell(row, 1).Value = "А. ПЛИТЫ НА ФАСАДЕ";
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 1).Style.Font.FontSize = 12;
            ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightSteelBlue;
            row++;

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

            var remnantsByParent = new Dictionary<string, List<RemnantDto>>();
            foreach (var r in createdRemnants)
            {
                if (string.IsNullOrEmpty(r.BaseBlockNumber)) continue;
                string parentKey = r.SuffixCounter <= 1
                    ? r.BaseBlockNumber
                    : $"{r.BaseBlockNumber}.{(r.SuffixCounter - 1):D2}";
                if (!remnantsByParent.ContainsKey(parentKey))
                    remnantsByParent[parentKey] = new List<RemnantDto>();
                remnantsByParent[parentKey].Add(r);
            }

            foreach (var tile in allTiles.OrderBy(t => t.RowIndex).ThenBy(t => t.PositionX))
            {
                ws.Cell(row, 1).Value = tile.BlockNumber;
                ws.Cell(row, 2).Value = tile.RowIndex + 1;
                ws.Cell(row, 3).Value = $"{tile.Width:F0}x{tile.Height:F0}";
                ws.Cell(row, 4).Value = tile.Width * tile.Height / 1_000_000.0;
                ws.Cell(row, 5).Value = tile.IsReused ? "Остаток" : "Новая";
                ws.Cell(row, 6).Value = tile.ParentInfo;

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
                    ws.Cell(row, 7).Value = "отход";
                    ws.Cell(row, 8).Value = $"{wasteW:F0}x{wasteH:F0}";
                    ws.Cell(row, 7).Style.Font.FontColor = XLColor.Red;
                    ws.Cell(row, 8).Style.Font.FontColor = XLColor.Red;
                }

                if (tile.IsReused)
                    ws.Range(row, 1, row, 8).Style.Fill.BackgroundColor = XLColor.LightGreen;
                row++;
            }

            row += 2;

            var toStock = createdRemnants.Where(r => !r.IsUsed).ToList();
            if (toStock.Any())
            {
                ws.Cell(row, 1).Value = "Б. СОЗДАННЫЕ ОСТАТКИ (НА СКЛАД)";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 1).Style.Font.FontSize = 12;
                ws.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightGreen;
                row++;

                ws.Cell(row, 1).Value = "Номер";
                ws.Cell(row, 2).Value = "Ширина";
                ws.Cell(row, 3).Value = "Высота";
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
            }

            ws.Columns().AdjustToContents();
        }

        static string FormatRemnantDisplayNumber(RemnantDto r)
        {
            if (!string.IsNullOrEmpty(r.BaseBlockNumber))
            {
                return r.SuffixCounter > 0
                    ? $"{r.BaseBlockNumber}.{r.SuffixCounter:D2}"
                    : r.BaseBlockNumber;
            }
            return r.Id.ToString().Substring(0, 8);
        }

        // ========= Helpers =========

        static void WriteMovementHeader(IXLWorksheet ws, int row)
        {
            ws.Cell(row, 1).Value = "Номер (X.Y.NNN.ZZ)";
            ws.Cell(row, 2).Value = "Ширина";
            ws.Cell(row, 3).Value = "Высота";
            ws.Cell(row, 4).Value = "Площадь (м²)";
            ws.Cell(row, 5).Value = "Статус";
            ws.Cell(row, 6).Value = "Источник";
            ws.Range(row, 1, row, 6).Style.Font.Bold = true;
        }

        static void WriteStatRow(IXLWorksheet ws, ref int row, string label, string value)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 2).Value = value;
            ws.Cell(row, 1).Style.Font.Bold = true;
            row++;
        }

        static void Log(string message)
        {
            try
            {
                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LogPath, entry + Environment.NewLine);
                Console.WriteLine(entry);
            }
            catch { }
        }
    }
}
