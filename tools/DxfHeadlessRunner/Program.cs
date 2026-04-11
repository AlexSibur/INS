using System.Globalization;
using InsulationMasterPro.Data;
using InsulationMasterPro.Models;
using InsulationMasterPro.OrTools;

if (args.Length < 1)
{
    Console.WriteLine("Usage: DxfHeadlessRunner <path-to-dxf>");
    return 2;
}

var dxfPath = Path.GetFullPath(args[0]);
if (!File.Exists(dxfPath))
{
    Console.WriteLine($"DXF not found: {dxfPath}");
    return 2;
}

Console.WriteLine($"[HEADLESS] Loading DXF: {dxfPath}");
var contoursWithLayers = ReadClosedLwPolylinesWithLayer(dxfPath);
if (contoursWithLayers.Length == 0)
{
    Console.WriteLine("[HEADLESS] No closed LWPOLYLINE contours found in DXF.");
    return 3;
}

// Split into facades and windows by layer
var facades = contoursWithLayers
    .Where(c => c.Layer.Contains("Внешний контур фасада", StringComparison.OrdinalIgnoreCase))
    .Select(c => c.Contour)
    .ToList();

var windows = contoursWithLayers
    .Where(c => c.Layer.Contains("Контуры окон", StringComparison.OrdinalIgnoreCase))
    .Select(c => c.Contour)
    .ToList();

Console.WriteLine($"[HEADLESS] Found {facades.Count} facades and {windows.Count} windows");

var constraints = new OptimizationConstraints();
int overallResult = 0;
List<double>? syncedRowHeights = null;
bool stockFlushLastFacade = false; // Stock Flush ОТКЛЮЧЁН: все фасады обязаны использовать Row Sync для углового замыкания
Console.WriteLine($"[CONFIG] Row Sync: ON (все фасады), Stock Flush: OFF (угловое замыкание требует единую сетку рядов)");

// Единый склад для всех фасадов (как в реальной работе):
// остатки от фасада 1 переходят в пул фасада 2 и т.д.
RemnantDatabase.ClearAll();
var stock = new List<Remnant>();
Console.WriteLine("[HEADLESS] Склад очищен один раз перед всеми фасадами (общий пул остатков)");

// === A2 FIX: Pre-scan фасадов для оценки demand (чистая площадь утепления) ===
// Это позволяет распределять остатки пропорционально между фасадами,
// а не отдавать весь склад последнему фасаду.
var facadeDemands = new List<double>(); // чистая площадь утепления каждого фасада (мм²)
for (int fi = 0; fi < facades.Count; fi++)
{
    var f = facades[fi];
    var fw = windows.Where(w =>
    {
        double wCenterX = (w.MinX + w.MaxX) / 2;
        double wCenterY = (w.MinY + w.MaxY) / 2;
        return wCenterX >= f.MinX && wCenterX <= f.MaxX &&
               wCenterY >= f.MinY && wCenterY <= f.MaxY;
    }).ToList();
    double facadeArea = f.Width * f.Height;
    double windowsArea = fw.Sum(w => w.Area);
    double netArea = facadeArea - windowsArea;
    facadeDemands.Add(netArea);
}
double totalDemand = facadeDemands.Sum();
Console.WriteLine($"[A2-DEMAND] Оценка demand по фасадам:");
for (int fi = 0; fi < facadeDemands.Count; fi++)
{
    double pct = totalDemand > 0 ? facadeDemands[fi] / totalDemand * 100.0 : 0;
    Console.WriteLine($"  Фасад {fi + 1}: {facadeDemands[fi] / 1_000_000.0:F3} м² ({pct:F1}%)");
}

// Сбор результатов для итоговой сводки (включая движение склада)
var allResults = new List<(int FacadeIndex, FacadeInput Input, LayoutResult Result,
    List<string> TechViolations, int StockIn, int RemovedCount, int AddedCount)>();

for (int facadeIndex = 0; facadeIndex < facades.Count; facadeIndex++)
{
    var facade = facades[facadeIndex];
    Console.WriteLine($"\n[HEADLESS] === Processing Facade {facadeIndex + 1}/{facades.Count} ===");
    
    // Find windows that are inside this facade
    var facadeWindows = windows
        .Where(w => 
        {
            double wCenterX = (w.MinX + w.MaxX) / 2;
            double wCenterY = (w.MinY + w.MaxY) / 2;
            return wCenterX >= facade.MinX && wCenterX <= facade.MaxX &&
                   wCenterY >= facade.MinY && wCenterY <= facade.MaxY;
        })
        .ToList();

    int stockInCount = stock.Count;
    double stockInAreaM2 = stock.Sum(r => r.Area) / 1_000_000.0;
    Console.WriteLine($"[HEADLESS] Facade {facadeIndex + 1}: {facade.Width:F0} x {facade.Height:F0} mm");
    Console.WriteLine($"[HEADLESS] Windows for this facade: {facadeWindows.Count}");
    Console.WriteLine($"[HEADLESS] Склад на входе: {stockInCount} остатков ({stockInAreaM2:F3} м²)");

    if (facadeWindows.Count == 0)
    {
        Console.WriteLine($"[HEADLESS] WARNING: No windows found for facade {facadeIndex + 1}, skipping");
        continue;
    }

    // Convert windows to WindowInfo
    var windowInfos = facadeWindows
        .Select((w, i) => new WindowInfo
        {
            Id = $"f{facadeIndex}_w{i}",
            MinX = w.MinX,
            MinY = w.MinY,
            MaxX = w.MaxX,
            MaxY = w.MaxY
        })
        .ToList();

    var input = new FacadeInput
    {
        FacadeId = $"Facade_{facadeIndex + 1}_" + Path.GetFileNameWithoutExtension(dxfPath),
        FacadeIndex = facadeIndex + 1,
        Width = facade.Width,
        Height = facade.Height,
        MinX = facade.MinX,
        MinY = facade.MinY,
        TrueFacadeAreaM2 = facade.Area / 1_000_000.0,
        TrueWindowsAreaM2 = facadeWindows.Sum(w => w.Area) / 1_000_000.0,
        StockRemnants = stock,
        Constraints = constraints,
        FirstRowWithRelease = facadeIndex % 2 == 0, // зеркалирование выпусков: нечётные фасады = true, чётные = false
        Windows = windowInfos
    };

    // Stock Flush FIX: последний фасад выходит из Row Sync и получает независимый RowHeightForecaster
    bool isLastFacade = (facadeIndex == facades.Count - 1);
    bool useStockFlush = stockFlushLastFacade && isLastFacade && facades.Count > 1;
    var currentSyncedHeights = (facadeIndex > 0 && !useStockFlush) ? syncedRowHeights : null;

    if (useStockFlush)
    {
        Console.WriteLine($"[STOCK-FLUSH] Фасад {facadeIndex + 1}: Stock Flush — независимая сетка рядов для максимальной утилизации остатков");
    }
    else if (currentSyncedHeights != null)
    {
        Console.WriteLine($"[ROW-SYNC] Фасад {facadeIndex + 1}: применяем сетку рядов от Фасада 1 (RowHeightForecaster пропущен)");
    }
    else if (facadeIndex == 0)
    {
        Console.WriteLine($"[ROW-SYNC] Фасад 1: сетка рядов рассчитывается независимо, учитывая окна ВСЕХ фасадов.");
    }
    
    // Генерируем массив глобальных окон для 1 фасада
    List<WindowInfo> forecasterWindows = input.Windows;
    if (facadeIndex == 0)
    {
        forecasterWindows = new List<WindowInfo>();
        var facade1MinY = facades[0].MinY;
        int globalWinId = 0;
        for (int fi = 0; fi < facades.Count; fi++)
        {
            var f = facades[fi];
            var fw = windows.Where(w => 
                (w.MinX + w.MaxX) / 2 >= f.MinX && (w.MinX + w.MaxX) / 2 <= f.MaxX &&
                (w.MinY + w.MaxY) / 2 >= f.MinY && (w.MinY + w.MaxY) / 2 <= f.MaxY).ToList();

            foreach (var w in fw)
            {
                double relMinY = w.MinY - f.MinY;
                double relMaxY = w.MaxY - f.MinY;
                forecasterWindows.Add(new WindowInfo
                {
                    Id = $"global_w{globalWinId++}",
                    MinX = w.MinX - f.MinX + facades[0].MinX,
                    MaxX = w.MaxX - f.MinX + facades[0].MinX,
                    MinY = relMinY + facade1MinY,
                    MaxY = relMaxY + facade1MinY
                });
            }
        }
        Console.WriteLine($"[ROW-SYNC] Спроецировано окон для RowHeightForecaster: {forecasterWindows.Count} шт.");
    }

    input.Rows = Preprocessor.DivideIntoRows(
        input.Width,
        input.Height,
        input.MinY,
        constraints.TileHeight,
        constraints.MinRowHeight,
        input.FirstRowWithRelease,
        facadeIndex == 0 ? forecasterWindows : input.Windows,
        input.StockRemnants,
        constraints,
        currentSyncedHeights);

    if (facadeIndex == 0)
    {
        syncedRowHeights = input.Rows.Select(r => r.Height).ToList();
        Console.WriteLine($"[ROW-SYNC] Захвачена сетка Фасада 1: {syncedRowHeights.Count} рядов → будет применена к Фасадам 2-{facades.Count}");
        Console.WriteLine($"[ROW-SYNC] Захвачена сетка: [{string.Join(", ", syncedRowHeights)}] мм (снизу вверх)");
    }

    foreach (var row in input.Rows)
    {
        row.RowType = Preprocessor.ClassifyRow(row, input.Windows);
        row.Segments = Preprocessor.CreateSegments(
            row,
            input.MinX,
            input.MinX + input.Width,
            input.Windows,
            constraints);
    }

    Console.WriteLine($"[HEADLESS] Rows: {input.Rows.Count}, Segments: {input.AllSegments.Count}");

    // Распределение высот рядов
    var rowHeightGroups = input.Rows
        .GroupBy(r => (int)Math.Round(r.Height / 10.0) * 10)
        .OrderBy(g => g.Key)
        .Select(g => $"{g.Key}мм×{g.Count()}");
    Console.WriteLine($"[HEADLESS]   Высоты рядов: {string.Join(", ", rowHeightGroups)}");

    // A2 FIX: Demand-proportional MaxRemnantUsageWeight boost для ранних фасадов.
    // Ранние фасады получают бóльший RemnantUsageWeight, чтобы сильнее стремиться использовать остатки.
    // Последний фасад использует стандартный вес (остатки уже распределены на ранних).
    double demandRatio = totalDemand > 0 ? facadeDemands[facadeIndex] / totalDemand : 1.0 / facades.Count;
    // Для ранних фасадов (меньше накопленного склада) увеличиваем вес;
    // для последних (больше склада) — стандартный вес.
    // facadeBoostFactor: facade 1 → 1.3, facade N → 1.0 (линейная интерполяция)
    double facadeBoostFactor = facades.Count > 1
        ? 1.3 - 0.3 * facadeIndex / (facades.Count - 1.0)
        : 1.0;
    // [Variant C] SBM и USPF удалены. Continuous bonus остаётся единственным механизмом.
    // Высокий множитель необходим: row-level objective использует ceil(), поэтому
    // мелкие остатки не снижают кол-во плит без сильного стимула.
    int boostedMaxRemnantWeight = Math.Min(137, (int)(135 * facadeBoostFactor));

    // Timeout для фасада (Stock Flush отключён — единый timeout)
    int facadeTimeout = 90;

    var config = new OptimizerConfig
    {
        TimeoutSeconds = facadeTimeout,
        LogProgress = false,
        UseRollingHorizon = true,
        EnableNarrowStripBonus = true, // приоритет узким полосам (1200×200мм) в CP-SAT objective
        EnableRemnantPreAllocation = true, // C1: включаем pre-allocation
        MaxStockForSolver = 400,
        MaxRemnantUsageWeight = boostedMaxRemnantWeight
    };
    Console.WriteLine($"[HEADLESS]   MaxStockForSolver: {config.MaxStockForSolver} (склад: {stock.Count} шт.)");
    Console.WriteLine($"[A2-BOOST]   Фасад {facadeIndex + 1}: demandRatio={demandRatio:F3}, boostFactor={facadeBoostFactor:F2}, " +
                      $"MaxRemnantUsageWeight={boostedMaxRemnantWeight}, timeout={facadeTimeout}s");

    var rolling = new RollingHorizonEngine(config, constraints)
    {
        HorizonRows = config.RollingHorizonRows,
        TimeoutPerWindowSeconds = config.RollingHorizonTimeoutPerWindowSeconds,
        MultiPassEnabled = true
    };

    Console.WriteLine($"[HEADLESS] Starting optimization for facade {facadeIndex + 1}...");
    var solution = rolling.Optimize(input, stock);
    var result = Postprocessor.ConvertToLayoutResult(solution, input, stock);

    // Сохранить диагностику для этого фасада
    DiagnosticLogger.WriteDiagnostics(input, solution, result, stock);

    // Списать использованные остатки со склада
    var usedRemnantIds = result.AllTiles
        .Where(t => t.IsReused && t.SourceRemnantId.HasValue)
        .Select(t => t.SourceRemnantId!.Value)
        .ToHashSet();
    int removedCount = stock.RemoveAll(r => usedRemnantIds.Contains(r.Id));

    // Пополнить склад созданными остатками (не-отходы)
    var savedRemnants = result.CreatedRemnants
        .Where(r => r.Fate != PieceFate.Waste)
        .ToList();
    stock.AddRange(savedRemnants);
    Console.WriteLine($"[HEADLESS]   Движение склада: вход={stockInCount}  -{removedCount}исп  +{savedRemnants.Count}созд  ={stock.Count}итого  " +
                      $"({stock.Sum(r => r.Area) / 1_000_000.0:F3} м²)");

    PrintStockDiag(stock, facadeIndex);

    Console.WriteLine($"[HEADLESS] Facade {facadeIndex + 1} Results:");
    Console.WriteLine($"[HEADLESS]   SolverStatus: {result.SolverStatus}, SolveTimeMs: {result.SolveTimeMs}");
    Console.WriteLine($"[HEADLESS]   NewTiles: {result.NewTilesCount}, Reused: {result.ReusedRemnantsCount}");
    Console.WriteLine($"[HEADLESS]   Overconsumption: {result.OverconsumptionPct:F1}% (limit-exceeded={result.OverConsumptionExceeded})");

    var techViolations = result.Errors
        .Concat(result.Warnings)
        .Where(IsTechViolation)
        .ToList();

    allResults.Add((facadeIndex, input, result, techViolations, stockInCount, removedCount, savedRemnants.Count));

    if (techViolations.Count > 0)
    {
        Console.WriteLine($"[HEADLESS]   TECH REQUIREMENTS: FAIL");
        foreach (var violation in techViolations.Take(12))
            Console.WriteLine($"[HEADLESS]     - {violation}");
        overallResult = 6;
    }
    else if (!string.Equals(result.SolverStatus, SolverStatus.Optimal.ToString(), StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(result.SolverStatus, SolverStatus.Feasible.ToString(), StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"[HEADLESS]   OPTIMIZATION: FAIL (solver has no feasible solution)");
        if (overallResult == 0) overallResult = 7;
    }
    else
    {
        Console.WriteLine($"[HEADLESS]   OPTIMIZATION: PASS");
        Console.WriteLine($"[HEADLESS]   TECH REQUIREMENTS: PASS");
    }
}

// ── Excel-отчёт один раз после всех фасадов ──────────────────────────────
if (allResults.Count > 0)
{
    try
    {
        var lastResult = allResults.Last().Result;
        var initialStockSnapshot = new List<Remnant>(); // склад был пуст в начале
        ExcelMovementReporter.GenerateFromLayoutResult(lastResult, initialStockSnapshot);
        Console.WriteLine("\n[HEADLESS] Excel report: C:\\DB_INS\\remnants.xlsx");
    }
    catch (Exception excelEx)
    {
        Console.WriteLine($"\n[HEADLESS] Excel report failed: {excelEx.Message}");
    }
}

// ── Итоговая сводная таблица ──────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("╔═══════╦════════════╦════════════╦════════════╦══════════╦══════════╦══════════╦═══════════════╗");
Console.WriteLine("║ Фасад ║  Перерасх  ║  Куплено   ║  Нетто м²  ║ Скл.вход ║  -Исп    ║  +Созд   ║  Тех.ошибки   ║");
Console.WriteLine("╠═══════╬════════════╬════════════╬════════════╬══════════╬══════════╬══════════╬═══════════════╣");

foreach (var (fi, inp, res, tv, sIn, sRem, sAdd) in allResults)
{
    string overcons = $"{res.OverconsumptionPct:F1}%{(res.OverConsumptionExceeded ? "!" : " ")}".PadRight(10);
    string bought   = $"{res.TotalNewMaterialArea:F2} м²".PadRight(10);
    string netto    = $"{res.NetInsulationArea:F2} м²".PadRight(10);
    string sInStr   = sIn.ToString().PadRight(8);
    string sRemStr  = $"-{sRem}".PadRight(8);
    string sAddStr  = $"+{sAdd}".PadRight(8);
    string techErr  = (tv.Count == 0 ? "PASS" : $"FAIL ({tv.Count})").PadRight(13);
    Console.WriteLine($"║ {fi + 1,-5} ║ {overcons} ║ {bought} ║ {netto} ║ {sInStr} ║ {sRemStr} ║ {sAddStr} ║ {techErr} ║");
}

Console.WriteLine("╚═══════╩════════════╩════════════╩════════════╩══════════╩══════════╩══════════╩═══════════════╝");

// ── Итоговые показатели ───────────────────────────────────────────────────
double totalNewMat    = allResults.Sum(r => r.Result.TotalNewMaterialArea);
double totalNetArea   = allResults.Sum(r => r.Result.NetInsulationArea);
double totalWaste     = allResults.Sum(r => r.Result.WasteArea);
double totalReused    = allResults.Sum(r => r.Result.ReusedArea);
double combinedPct    = totalNetArea > 0 ? (totalNewMat / totalNetArea * 100.0 - 100.0) : 0;
int    stockCount     = stock.Count;
double stockAreaM2    = stock.Sum(r => r.Area) / 1_000_000.0;

Console.WriteLine();
Console.WriteLine($"  Куплено нового материала (всего) : {totalNewMat:F3} м²");
Console.WriteLine($"  Чистая площадь утепления (всего) : {totalNetArea:F3} м²");
Console.WriteLine($"  Использовано остатков со склада  : {totalReused:F3} м²");
Console.WriteLine($"  Безвозвратные отходы (всего)     : {totalWaste:F3} м²");
Console.WriteLine($"  ОБЩИЙ ПЕРЕРАСХОД (4 фасада)      : {combinedPct:F2}% {(combinedPct > 7.0 ? "[ПРЕВЫШЕН лимит 7%]" : "[OK]")}");
Console.WriteLine($"  На складе после всех фасадов     : {stockCount} остатков, {stockAreaM2:F3} м²");
Console.WriteLine();

// ── Диагностика финального склада ────────────────────────────────────────
const double minClassA = 200.0; // пригоден для любых рядов
const double minClassB = 150.0; // пригоден только в оконных зонах
var classA = stock.Where(r => Math.Min(r.Width, r.Height) >= minClassA).ToList();
var classB = stock.Where(r => Math.Min(r.Width, r.Height) >= minClassB && Math.Min(r.Width, r.Height) < minClassA).ToList();

Console.WriteLine("  ДИАГНОСТИКА СКЛАДА:");
Console.WriteLine($"  Класс A (мин.сторона ≥200 мм) — любые ряды  : {classA.Count,4} шт., {classA.Sum(r => r.Area) / 1_000_000.0:F3} м²");
Console.WriteLine($"  Класс B (мин.сторона 150-199 мм) — только окна: {classB.Count,4} шт., {classB.Sum(r => r.Area) / 1_000_000.0:F3} м²");

// Топ-5 размеров на складе
var top5 = stock
    .GroupBy(r => ($"{Math.Round(r.Width / 10) * 10:F0}", $"{Math.Round(r.Height / 10) * 10:F0}"))
    .OrderByDescending(g => g.Count())
    .Take(5)
    .ToList();
if (top5.Count > 0)
{
    Console.WriteLine("  Топ-5 размеров на складе:");
    foreach (var g in top5)
        Console.WriteLine($"    {g.Key.Item1}×{g.Key.Item2} мм — {g.Count()} шт., {g.Sum(r => r.Area) / 1_000_000.0:F3} м²");
}
Console.WriteLine();

// ── Детальный список нарушений (если есть) ────────────────────────────────
var allViolations = allResults.SelectMany(r => r.TechViolations.Select(v => $"  Фасад {r.FacadeIndex + 1}: {v}")).ToList();
if (allViolations.Count > 0)
{
    Console.WriteLine("  ТЕХНИЧЕСКИЕ НАРУШЕНИЯ:");
    foreach (var v in allViolations)
        Console.WriteLine(v);
    Console.WriteLine();
}

// ── Итог ─────────────────────────────────────────────────────────────────
Console.WriteLine($"[HEADLESS] === Все фасады обработаны ===");
if (overallResult == 0)
    Console.WriteLine("[HEADLESS] OVERALL RESULT: PASS");
else if (overallResult == 6)
    Console.WriteLine("[HEADLESS] OVERALL RESULT: TECH REQUIREMENTS FAILED");
else if (overallResult == 7)
    Console.WriteLine("[HEADLESS] OVERALL RESULT: OPTIMIZATION FAILED");

return overallResult;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static void PrintStockDiag(List<Remnant> stock, int facadeIndex)
{
    double minClassA = 200.0;
    double minClassB = 150.0;
    var classA = stock.Where(r => Math.Min(r.Width, r.Height) >= minClassA).ToList();
    var classB = stock.Where(r => Math.Min(r.Width, r.Height) >= minClassB && Math.Min(r.Width, r.Height) < minClassA).ToList();
    var dead = stock.Where(r => Math.Min(r.Width, r.Height) < minClassB).ToList();
    
    double areaA = classA.Sum(x => x.Area) / 1000000.0;
    double areaB = classB.Sum(x => x.Area) / 1000000.0;
    double areaDead = dead.Sum(x => x.Area) / 1000000.0;
    double totalArea = stock.Sum(x => x.Area) / 1000000.0;
    double deadPct = totalArea > 0 ? (areaDead / totalArea) * 100.0 : 0;
    
    Console.WriteLine($"[STOCK-DIAG] Фасад {facadeIndex + 1}: склад {stock.Count} шт.");
    Console.WriteLine($"  → Класс A (≥200мм) : {classA.Count} шт. ({areaA:F3} м²) — пригодны для любых рядов");
    Console.WriteLine($"  → Класс B (150-199мм): {classB.Count} шт. ({areaB:F3} м²) — только оконные зоны");
    Console.WriteLine($"  → Мёртвые (<150мм)  : {dead.Count} шт. ({areaDead:F3} м²) — НЕ могут быть использованы");
    Console.WriteLine($"  → \"Мёртвый\" склад (% от общей площади): {deadPct:F1}%");
}


static bool IsTechViolation(string message)
{
    if (string.IsNullOrWhiteSpace(message))
        return false;

    return message.StartsWith("E_MIDDLE_ROW_WINDOW_ZONE_FILLED", StringComparison.OrdinalIgnoreCase) ||
           message.StartsWith("E_LBOOT_SHELF_SIZE", StringComparison.OrdinalIgnoreCase) ||
           message.StartsWith("E_WINDOW_PERIMETER_NOT_COVERED", StringComparison.OrdinalIgnoreCase) ||
           message.StartsWith("E_OVERLAP_COORD_ERROR", StringComparison.OrdinalIgnoreCase);
}

static (ClosedContour Contour, string Layer)[] ReadClosedLwPolylinesWithLayer(string dxfPath)
{
    var lines = File.ReadAllLines(dxfPath);
    var result = new List<(ClosedContour, string)>();
    var ci = NumberFormatInfo.InvariantInfo;

    for (int i = 0; i < lines.Length; i++)
    {
        string line = lines[i].Trim();
        if (line != "LWPOLYLINE") continue;

        int closed = 0;
        var xs = new List<double>();
        var ys = new List<double>();
        string layer = "";

        int j = i + 1;
        while (j < lines.Length)
        {
            string codeStr = lines[j].Trim();
            j++;
            if (j >= lines.Length) break;
            string valueStr = lines[j].Trim();
            j++;

            if (!int.TryParse(codeStr, NumberStyles.Integer, ci, out int code))
                continue;

            if (code == 0)
            {
                j -= 2;
                break;
            }
            if (code == 8) // Layer
            {
                layer = valueStr;
            }
            else if (code == 70)
            {
                if (int.TryParse(valueStr, NumberStyles.Integer, ci, out int flags))
                    closed = (flags & 1);
            }
            else if (code == 10)
            {
                if (double.TryParse(valueStr, NumberStyles.Float, ci, out double x))
                    xs.Add(x);
            }
            else if (code == 20)
            {
                if (double.TryParse(valueStr, NumberStyles.Float, ci, out double y))
                    ys.Add(y);
            }
        }

        if (closed != 1 || xs.Count < 3 || xs.Count != ys.Count) continue;

        var vertices = new List<(double X, double Y)>();
        for (int k = 0; k < xs.Count; k++)
            vertices.Add((xs[k], ys[k]));

        double area = Math.Abs(ShoelaceArea(vertices));
        result.Add((
            new ClosedContour
            {
                Area = area,
                MinX = xs.Min(),
                MinY = ys.Min(),
                MaxX = xs.Max(),
                MaxY = ys.Max(),
                Vertices = vertices
            },
            layer
        ));
    }

    return result.ToArray();
}

static double ShoelaceArea(List<(double X, double Y)> v)
{
    double sum = 0;
    for (int i = 0; i < v.Count; i++)
    {
        int j = (i + 1) % v.Count;
        sum += v[i].X * v[j].Y - v[j].X * v[i].Y;
    }
    return sum / 2.0;
}

internal sealed class ClosedContour
{
    public double Area { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public List<(double X, double Y)> Vertices { get; set; } = new();
}
