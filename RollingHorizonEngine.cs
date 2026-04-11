using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.OrTools
{
    /// <summary>
    /// Последовательная оптимизация со «скользящим окном» (Rolling Horizon).
    /// Решаем сразу 2–3 ряда, коммитим только первый, обновляем склад и сдвигаем окно.
    /// Поддержка двухпроходного режима: Pass 1 (стандартный) + Pass 2 (агрессивная утилизация остатков).
    /// </summary>
    public class RollingHorizonEngine
    {
        private readonly OptimizerConfig _config;
        private readonly OptimizationConstraints _constraints;
        /// <summary>Собранные L-boot элементы во время текущего прохода (ТЗ v13.1 §14).</summary>
        private List<LBootElement> _lastLBootElements = new List<LBootElement>();

        /// <summary>
        /// Размер окна: сколько рядов решаем за один вызов (sliding window).
        /// Солвер видит HorizonRows рядов одновременно, коммитит только первый.
        /// HorizonRows=1 — стандартное поведение (каждый ряд независимо, максимальная скорость).
        /// HorizonRows=2..3 — look-ahead для лучшей утилизации остатков через ряды,
        ///   требует уменьшить TimeoutPerWindowSeconds (иначе время ×2–3).
        ///   Рекомендуется включать только после профилирования на конкретной фасаде.
        /// </summary>
        public int HorizonRows { get; set; } = 1;

        /// <summary>Таймаут на одно окно (секунды).</summary>
        public int TimeoutPerWindowSeconds { get; set; } = 5;

        /// <summary>Включить двухпроходный режим (Pass 1: стандартный, Pass 2: агрессивная утилизация остатков).</summary>
        public bool MultiPassEnabled { get; set; } = true;

        /// <summary>
        /// Включить пост-процессор консолидации (Intra-Row BinPack).
        /// Группирует мелкие [New] блоки из разных сегментов одного ряда в минимум физических плит.
        /// Пример: 4 блока по 200-240 мм → 1 физическая плита вместо 4.
        /// Снижает Overconsumption с ~6% до ~0.8% на типовой фасаде 15×10 м.
        /// По умолчанию включён.
        /// </summary>
        public bool EnableIntraRowConsolidate { get; set; } = true;

        public RollingHorizonEngine(OptimizerConfig config = null, OptimizationConstraints constraints = null)
        {
            _config = config ?? new OptimizerConfig();
            _constraints = constraints ?? new OptimizationConstraints();
        }

        /// <summary>
        /// Результат одного прохода RH (для сравнения между проходами).
        /// </summary>
        private class PassResult
        {
            public List<RowSolution> CommittedRows;
            public List<Remnant> CurrentStock;
            public List<Remnant> AllCreatedRemnants;
            public long TotalSolveTimeMs;
            public int TotalNewTiles;
            public double TotalCreatedRemnantArea;
            /// <summary>Суммарная площадь остатков склада, использованных в этом проходе.</summary>
            public double TotalReusedRemnantArea;
            public bool IsError;
            public OrToolsSolution ErrorSolution;
            public List<LBootElement> LBootElements = new List<LBootElement>();
        }

        /// <summary>
        /// Оптимизирует раскладку фасада методом скользящего окна.
        /// При MultiPassEnabled запускает два прохода и выбирает лучший.
        /// </summary>
        public OrToolsSolution Optimize(
            Polyline facade,
            List<Polyline> windows,
            List<Remnant> stockRemnants,
            bool firstRowWithRelease = true,
            List<double>? syncedRowHeights = null,
            List<WindowInfo>? globalForecasterWindows = null)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var fullInput = Preprocessor.ConvertToFacadeInput(
                    facade, windows, stockRemnants, firstRowWithRelease, _constraints, syncedRowHeights, globalForecasterWindows);

                if (fullInput.Rows.Count == 0)
                {
                    return new OrToolsSolution
                    {
                        FacadeId = fullInput.FacadeId,
                        Status = SolverStatus.Infeasible,
                        SolveTimeMs = stopwatch.ElapsedMilliseconds,
                        Diagnostics = new List<string> { "Нет рядов после препроцессора." }
                    };
                }

                // === Адаптивные параметры на основе сложности фасада ===
                // Sweep 10мм с предфильтром даёт ~600-800 валидных позиций/остаток.
                // Бюджет 10000: полное покрытие 10+ остатков; 41 сегмент × 10000 = 410 000 паттернов.
                int totalSegments = fullInput.Rows.Sum(r => r.Segments.Count);
                int adaptiveMaxPat = Math.Min(_config.MaxPatternsPerSegment, 10_000);
                int adaptiveTimeout = Math.Max(15, TimeoutPerWindowSeconds * 3);

                // === Проход 1: стандартные веса ===
                // [Variant C] SBM и USPF удалены, continuous bonus — единственный механизм.
                // Pass1 = MaxRUW-20 (умеренный), Pass2 = MaxRUW (агрессивный).
                int effectiveTileWeight = _config.TileWeight > 0 ? _config.TileWeight : 10_000;
                int maxRemnantWeight = _config.MaxRemnantUsageWeight > 0 ? _config.MaxRemnantUsageWeight : 135;
                int pass1RemnantWeight = Math.Max(80, maxRemnantWeight - 20);
                var pass1Config = new OptimizerConfig
                {
                    TimeoutSeconds = adaptiveTimeout,
                    LogProgress = _config.LogProgress,
                    MaxPatternsPerSegment = adaptiveMaxPat,
                    TileWeight = effectiveTileWeight,
                    RemnantUsageWeight = pass1RemnantWeight,
                    MaxRemnantUsageWeight = maxRemnantWeight,
                    ImprovementCMinDim = _config.ImprovementCMinDim,
                    EnableRowLevelObjective = _config.EnableRowLevelObjective,
                    EnableNarrowStripBonus = _config.EnableNarrowStripBonus
                };

                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RollingHorizonEngine] pass1 weights " +
                    $"TileWeight={pass1Config.TileWeight} RemnantUsageWeight_pass1={pass1Config.RemnantUsageWeight} " +
                    $"ImprovementCMinDim={pass1Config.ImprovementCMinDim}");

                // B1: In-Memory Virtual Pre-Splitting
                var preSplit = ApplyInMemoryPreSplitting(stockRemnants);
                var inMemoryStock = preSplit.InMemStock;

                // === Greedy pre-allocation (опционально) ===
                if (_config.EnableRemnantPreAllocation && inMemoryStock.Count > 0)
                {
                    fullInput.RemnantRowHints = PreAllocateRemnants(fullInput, inMemoryStock);
                }

                var pass1 = RunSinglePass(fullInput, inMemoryStock, pass1Config, adaptiveTimeout);

                if (pass1.IsError)
                    return pass1.ErrorSolution;

                // === Проход 2: агрессивная утилизация остатков (если включен multi-pass) ===
                PassResult bestPass = pass1;

                if (MultiPassEnabled)
                {
                    // Pass2 использует MaxRemnantUsageWeight (агрессивная утилизация).
                    var pass2Config = new OptimizerConfig
                    {
                        TimeoutSeconds = adaptiveTimeout,
                        LogProgress = _config.LogProgress,
                        MaxPatternsPerSegment = adaptiveMaxPat,
                        TileWeight = pass1Config.TileWeight,
                        RemnantUsageWeight = maxRemnantWeight,
                        MaxRemnantUsageWeight = maxRemnantWeight,
                        ImprovementCMinDim = _config.ImprovementCMinDim,
                        EnableRowLevelObjective = _config.EnableRowLevelObjective,
                        EnableNarrowStripBonus = _config.EnableNarrowStripBonus
                    };

                    System.Diagnostics.Debug.WriteLine(
                        $"DEBUG [RollingHorizonEngine] pass2 weights " +
                        $"TileWeight={pass2Config.TileWeight} RemnantUsageWeight_pass2={pass2Config.RemnantUsageWeight} " +
                        $"ImprovementCMinDim={pass2Config.ImprovementCMinDim}");

                    var pass2 = RunSinglePass(fullInput, inMemoryStock, pass2Config, adaptiveTimeout);

                    if (!pass2.IsError)
                    {
                        // Выбираем лучший проход:
                        // PRIMARY   = меньше новых плит
                        // SECONDARY = больше использованных остатков склада
                        // TERTIARY  = меньше созданных остатков (меньше отходов)
                        bool pass2Wins =
                            pass2.TotalNewTiles < bestPass.TotalNewTiles ||
                            (pass2.TotalNewTiles == bestPass.TotalNewTiles &&
                             pass2.TotalReusedRemnantArea > bestPass.TotalReusedRemnantArea) ||
                            (pass2.TotalNewTiles == bestPass.TotalNewTiles &&
                             pass2.TotalReusedRemnantArea == bestPass.TotalReusedRemnantArea &&
                             pass2.TotalCreatedRemnantArea < bestPass.TotalCreatedRemnantArea);

                        System.Diagnostics.Debug.WriteLine(
                            $"DEBUG [RollingHorizonEngine] Pass comparison: " +
                            $"pass1={bestPass.TotalNewTiles}/{bestPass.TotalReusedRemnantArea:F4}/{bestPass.TotalCreatedRemnantArea:F4}, " +
                            $"pass2={pass2.TotalNewTiles}/{pass2.TotalReusedRemnantArea:F4}/{pass2.TotalCreatedRemnantArea:F4}, " +
                            $"winner={(pass2Wins ? "pass2" : "pass1")}");

                        if (pass2Wins)
                            bestPass = pass2;
                    }
                }

                // B1: Реконсиляция виртуально разделенных кусков (возврат неиспользованных и корректировка ID)
                ReconcileInMemoryPreSplitting(bestPass, preSplit.Map);

                // === Пост-оптимизация: переоптимизация слабых рядов ===
                PostOptimize(fullInput, bestPass, adaptiveTimeout, adaptiveMaxPat);

                stopwatch.Stop();

                var solution = BuildFinalSolution(
                    fullInput, bestPass.CommittedRows, bestPass.CurrentStock,
                    bestPass.AllCreatedRemnants, bestPass.TotalSolveTimeMs,
                    bestPass.LBootElements);

                return solution;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new OrToolsSolution
                {
                    FacadeId = "error",
                    Status = SolverStatus.Error,
                    SolveTimeMs = stopwatch.ElapsedMilliseconds,
                    Diagnostics = new List<string>
                    {
                        $"{ex.GetType().Name}: {ex.Message}",
                        ex.StackTrace ?? ""
                    }
                };
            }
        }

        /// <summary>
        /// Headless-совместимая перегрузка: принимает готовый FacadeInput без AutoCAD Polyline.
        /// </summary>
        public OrToolsSolution Optimize(FacadeInput fullInput, List<Remnant> stockRemnants)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                if (fullInput.Rows.Count == 0)
                {
                    return new OrToolsSolution
                    {
                        FacadeId = fullInput.FacadeId,
                        Status = SolverStatus.Infeasible,
                        SolveTimeMs = stopwatch.ElapsedMilliseconds,
                        Diagnostics = new List<string> { "Нет рядов после препроцессора." }
                    };
                }

                int totalSegments = fullInput.Rows.Sum(r => r.Segments.Count);
                int adaptiveMaxPat = Math.Min(_config.MaxPatternsPerSegment, 10_000);
                int adaptiveTimeout = Math.Max(15, TimeoutPerWindowSeconds * 3);

                // Headless-путь унифицирован с AutoCAD-путём: те же веса pass1/pass2.
                // [Variant C] SBM и USPF удалены, continuous bonus — единственный механизм.
                int effectiveTileWeightH = _config.TileWeight > 0 ? _config.TileWeight : 10_000;
                int maxRemnantWeightH = _config.MaxRemnantUsageWeight > 0 ? _config.MaxRemnantUsageWeight : 135;
                int pass1RemnantWeightH = Math.Max(80, maxRemnantWeightH - 20);

                var pass1Config = new OptimizerConfig
                {
                    TimeoutSeconds = adaptiveTimeout,
                    LogProgress = _config.LogProgress,
                    MaxPatternsPerSegment = adaptiveMaxPat,
                    TileWeight = effectiveTileWeightH,
                    RemnantUsageWeight = pass1RemnantWeightH,
                    MaxRemnantUsageWeight = maxRemnantWeightH,
                    ImprovementCMinDim = _config.ImprovementCMinDim,
                    EnableRowLevelObjective = _config.EnableRowLevelObjective
                };

                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RollingHorizonEngine] headless pass1 weights " +
                    $"TileWeight={pass1Config.TileWeight} RemnantUsageWeight={pass1Config.RemnantUsageWeight} " +
                    $"ImprovementCMinDim={pass1Config.ImprovementCMinDim}");

                // B1: In-Memory Virtual Pre-Splitting
                var preSplit = ApplyInMemoryPreSplitting(stockRemnants);
                var inMemoryStock = preSplit.InMemStock;

                // === Greedy pre-allocation (опционально) ===
                if (_config.EnableRemnantPreAllocation && inMemoryStock.Count > 0)
                {
                    fullInput.RemnantRowHints = PreAllocateRemnants(fullInput, inMemoryStock);
                }

                var pass1 = RunSinglePass(fullInput, inMemoryStock, pass1Config, adaptiveTimeout);

                if (pass1.IsError)
                    return pass1.ErrorSolution;

                PassResult bestPass = pass1;

                if (MultiPassEnabled)
                {
                    var pass2Config = new OptimizerConfig
                    {
                        TimeoutSeconds = adaptiveTimeout,
                        LogProgress = _config.LogProgress,
                        MaxPatternsPerSegment = adaptiveMaxPat,
                        TileWeight = pass1Config.TileWeight,
                        RemnantUsageWeight = maxRemnantWeightH,
                        MaxRemnantUsageWeight = maxRemnantWeightH,
                        ImprovementCMinDim = _config.ImprovementCMinDim,
                        EnableRowLevelObjective = _config.EnableRowLevelObjective
                    };

                    System.Diagnostics.Debug.WriteLine(
                        $"DEBUG [RollingHorizonEngine] headless pass2 weights " +
                        $"TileWeight={pass2Config.TileWeight} RemnantUsageWeight={pass2Config.RemnantUsageWeight}");

                    var pass2 = RunSinglePass(fullInput, inMemoryStock, pass2Config, adaptiveTimeout);

                    if (!pass2.IsError)
                    {
                        bool pass2WinsH =
                            pass2.TotalNewTiles < bestPass.TotalNewTiles ||
                            (pass2.TotalNewTiles == bestPass.TotalNewTiles &&
                             pass2.TotalReusedRemnantArea > bestPass.TotalReusedRemnantArea) ||
                            (pass2.TotalNewTiles == bestPass.TotalNewTiles &&
                             pass2.TotalReusedRemnantArea == bestPass.TotalReusedRemnantArea &&
                             pass2.TotalCreatedRemnantArea < bestPass.TotalCreatedRemnantArea);

                        System.Diagnostics.Debug.WriteLine(
                            $"DEBUG [RollingHorizonEngine] Headless pass comparison: " +
                            $"pass1={bestPass.TotalNewTiles}/{bestPass.TotalReusedRemnantArea:F4}/{bestPass.TotalCreatedRemnantArea:F4}, " +
                            $"pass2={pass2.TotalNewTiles}/{pass2.TotalReusedRemnantArea:F4}/{pass2.TotalCreatedRemnantArea:F4}, " +
                            $"winner={(pass2WinsH ? "pass2" : "pass1")}");

                        if (pass2WinsH)
                            bestPass = pass2;
                    }
                }

                // B1: Реконсиляция виртуально разделенных кусков (возврат неиспользованных и корректировка ID)
                ReconcileInMemoryPreSplitting(bestPass, preSplit.Map);

                PostOptimize(fullInput, bestPass, adaptiveTimeout, adaptiveMaxPat);

                stopwatch.Stop();

                var solution = BuildFinalSolution(
                    fullInput, bestPass.CommittedRows, bestPass.CurrentStock,
                    bestPass.AllCreatedRemnants, bestPass.TotalSolveTimeMs,
                    bestPass.LBootElements);

                return solution;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new OrToolsSolution
                {
                    FacadeId = "error",
                    Status = SolverStatus.Error,
                    SolveTimeMs = stopwatch.ElapsedMilliseconds,
                    Diagnostics = new List<string>
                    {
                        $"{ex.GetType().Name}: {ex.Message}",
                        ex.StackTrace ?? ""
                    }
                };
            }
        }
        private (List<Remnant> InMemStock, Dictionary<Guid, (Remnant Orig, Remnant R1, Remnant R2)> Map) ApplyInMemoryPreSplitting(List<Remnant> stock)
        {
            var inMemStock = new List<Remnant>();
            var map = new Dictionary<Guid, (Remnant Orig, Remnant R1, Remnant R2)>();
            foreach (var r in stock)
            {
                if (!r.IsUsed && Math.Abs(r.Width - 1200.0) < 1.0 && r.Height < 600.0)
                {
                    var r1 = new Remnant
                    {
                        Id = Guid.NewGuid(), Width = 600.0, Height = r.Height,
                        ParentRemnantId = r.Id, Source = r.Source,
                        CutHistory = r.CutHistory + (string.IsNullOrEmpty(r.CutHistory) ? "" : " ") + $"→(PreSplit)600x{r.Height:F0}",
                        AddedDate = r.AddedDate, FacadeIndex = r.FacadeIndex,
                        BaseBlockNumber = r.BaseBlockNumber, SuffixCounter = r.SuffixCounter, IsUsed = false
                    };
                    var r2 = new Remnant
                    {
                        Id = Guid.NewGuid(), Width = 600.0, Height = r.Height,
                        ParentRemnantId = r.Id, Source = r.Source,
                        CutHistory = r.CutHistory + (string.IsNullOrEmpty(r.CutHistory) ? "" : " ") + $"→(PreSplit)600x{r.Height:F0}",
                        AddedDate = r.AddedDate, FacadeIndex = r.FacadeIndex,
                        BaseBlockNumber = r.BaseBlockNumber, SuffixCounter = r.SuffixCounter + 1, IsUsed = false
                    };
                    inMemStock.Add(r1);
                    inMemStock.Add(r2);
                    map[r.Id] = (r, r1, r2);
                }
                else
                {
                    inMemStock.Add(r);
                }
            }
            return (inMemStock, map);
        }

        private void ReconcileInMemoryPreSplitting(PassResult bestPass, Dictionary<Guid, (Remnant Orig, Remnant R1, Remnant R2)> map)
        {
            if (map.Count == 0) return;
            var usedInPass = new HashSet<Guid>();
            foreach (var row in bestPass.CommittedRows)
                foreach(var seg in row.Segments)
                    foreach(var block in seg.Blocks)
                        if (block.Type == BlockType.CutRemnant && block.RemnantId.HasValue)
                            usedInPass.Add(block.RemnantId.Value);

            foreach (var kvp in map)
            {
                var orig = kvp.Value.Orig;
                var r1 = kvp.Value.R1;
                var r2 = kvp.Value.R2;
                bool used1 = usedInPass.Contains(r1.Id);
                bool used2 = usedInPass.Contains(r2.Id);

                if (used1 || used2)
                {
                    foreach (var row in bestPass.CommittedRows)
                    {
                        foreach (var seg in row.Segments)
                        {
                            foreach (var block in seg.Blocks)
                            {
                                if (block.Type == BlockType.CutRemnant && (block.RemnantId == r1.Id || block.RemnantId == r2.Id))
                                {
                                    block.RemnantId = orig.Id;
                                }
                            }
                        }
                    }
                    if (used1 && !used2)
                    {
                        r2.Fate = PieceFate.InStock;
                        bestPass.AllCreatedRemnants.Add(r2);
                    }
                    else if (!used1 && used2)
                    {
                        r1.Fate = PieceFate.InStock;
                        bestPass.AllCreatedRemnants.Add(r1);
                    }
                }
            }
        }

        /// <summary>
        /// Выполняет один проход Rolling Horizon с заданными параметрами.
        /// </summary>
        private PassResult RunSinglePass(
            FacadeInput fullInput,
            List<Remnant> stockRemnants,
            OptimizerConfig windowConfig,
            int adaptiveTimeout)
        {
            var committedRows = new List<RowSolution>();
            var currentStock = new List<Remnant>(stockRemnants);
            var allCreatedRemnants = new List<Remnant>();
            _lastLBootElements = new List<LBootElement>();
            int totalRows = fullInput.Rows.Count;
            int startRow = 0;
            long totalSolveTimeMs = 0;
            int totalNewTiles = 0;
            double totalCreatedArea = 0;
            double totalReusedArea = 0;

            // Improvement A: reserve cutout remnants from EdgeRows for future similar EdgeRows
            var reservedUntilRow = new Dictionary<Guid, int>();

            while (startRow < totalRows)
            {
                // Release reserved remnants whose target row has been reached
                foreach (var kvp in reservedUntilRow.Where(k => k.Value <= startRow).ToList())
                    reservedUntilRow.Remove(kvp.Key);

                // HorizonRows: решаем несколько рядов совместно, коммитим только первый (sliding window).
                // При timeout/infeasibility с horizon>1 — повторная попытка с horizon=1 (fallback).
                int adaptiveHorizon = HorizonRows;
                int endRow = Math.Min(startRow + adaptiveHorizon, totalRows);

                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RollingHorizonEngine] horizon={adaptiveHorizon} " +
                    $"rows=[{startRow}..{endRow - 1}] solving jointly");

                // Собираем ширины будущих сегментов (look-ahead) для рядов за пределами окна
                var futureWidths = new List<double>();
                int lookAheadEnd = Math.Min(endRow + 10, totalRows);
                for (int r = endRow; r < lookAheadEnd; r++)
                {
                    foreach (var seg in fullInput.Rows[r].Segments)
                        futureWidths.Add(seg.Width);
                }

                // Окно: только ряды [startRow .. endRow-1]
                var windowInput = BuildWindowInput(fullInput, startRow, endRow, currentStock);
                windowInput.FutureSegmentWidths = futureWidths;

                // Improvement A: exclude reserved remnants from solver input
                if (reservedUntilRow.Count > 0)
                {
                    int reservedCount = windowInput.StockRemnants.Count(r => reservedUntilRow.ContainsKey(r.Id));
                    if (reservedCount > 0)
                    {
                        windowInput.StockRemnants = windowInput.StockRemnants
                            .Where(r => !reservedUntilRow.ContainsKey(r.Id))
                            .ToList();
                        System.Diagnostics.Debug.WriteLine(
                            $"[RESERVE] Row {startRow}: filtered out {reservedCount} reserved remnants from solver input");
                    }
                }

                // === A2 FIX: Pre-cut simulation ===
                // Предсказываем остатки от оконных вырезов ПЕРЕД вызовом солвера,
                // чтобы они были доступны для использования в других сегментах этого же ряда.
                var virtualCutouts = PredictWindowCutoutRemnants(
                    fullInput.Rows[startRow], fullInput.Windows, fullInput.Constraints,
                    windowInput.StockRemnants);
                if (virtualCutouts.Count > 0)
                {
                    windowInput.StockRemnants.AddRange(virtualCutouts);
                    System.Diagnostics.Debug.WriteLine(
                        $"[A2] Row {startRow}: predicted {virtualCutouts.Count} virtual cutouts " +
                        $"({string.Join(", ", virtualCutouts.Select(r => $"{r.Width:F0}x{r.Height:F0}"))})");
                }

                // === Stock cap — ограничиваем склад для солвера ===
                // Стратегия: топ-N/2 по площади + топ-N/2 по соответствию текущему ряду.
                // Это гарантирует, что идеально подходящий маленький остаток не будет исключён
                // только из-за маленькой площади.
                int maxStock = _config.MaxStockForSolver;
                if (windowInput.StockRemnants.Count > maxStock)
                {
                    windowInput.StockRemnants = SelectStockForSolver(
                        windowInput.StockRemnants, windowInput.Rows, maxStock, startRow, _constraints);
                }

                var patterns = PatternGenerator.GeneratePatterns(windowInput, windowConfig);

                var segmentsWithoutPatterns = patterns.Where(kv => kv.Value.Count == 0).ToList();
                if (segmentsWithoutPatterns.Any())
                {
                    var diag = new List<string>
                    {
                        $"В окне рядов {startRow}..{endRow - 1} для сегментов нет допустимых паттернов."
                    };
                    foreach (var kv in segmentsWithoutPatterns)
                        diag.Add($"  Ряд {kv.Key.Item1}, сегмент {kv.Key.Item2}");
                    return new PassResult
                    {
                        IsError = true,
                        ErrorSolution = new OrToolsSolution
                        {
                            FacadeId = fullInput.FacadeId,
                            Status = SolverStatus.Infeasible,
                            SolveTimeMs = totalSolveTimeMs,
                            Diagnostics = diag
                        }
                    };
                }

                // Стыки предыдущего ряда — для перевязки
                IReadOnlyList<double> fixedPreviousJoints = null;
                if (startRow > 0)
                    fixedPreviousJoints = RowSolution.GetJointPositions(committedRows[startRow - 1]);

                var optimizer = new OrToolsOptimizer(windowInput, patterns, windowConfig);
                var windowSolution = optimizer.Solve(fixedPreviousJoints);

                // Retry без перевязки
                if (windowSolution.Status != SolverStatus.Optimal && windowSolution.Status != SolverStatus.Feasible
                    && fixedPreviousJoints != null)
                {
                    optimizer = new OrToolsOptimizer(windowInput, patterns, windowConfig);
                    windowSolution = optimizer.Solve(null);
                }

                // Fallback: если horizon>1 и решение не найдено — пробуем с horizon=1
                if (windowSolution.Status != SolverStatus.Optimal && windowSolution.Status != SolverStatus.Feasible
                    && adaptiveHorizon > 1)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"WARN [RollingHorizonEngine] horizon={adaptiveHorizon} failed ({windowSolution.Status}), " +
                        $"falling back to horizon=1 for row {startRow}");

                    adaptiveHorizon = 1;
                    endRow = startRow + 1;
                    var fbFutureWidths = new List<double>();
                    for (int r = endRow; r < Math.Min(endRow + 10, totalRows); r++)
                        foreach (var seg in fullInput.Rows[r].Segments)
                            fbFutureWidths.Add(seg.Width);

                    var fbWindowInput = BuildWindowInput(fullInput, startRow, endRow, currentStock);
                    fbWindowInput.FutureSegmentWidths = fbFutureWidths;
                    if (reservedUntilRow.Count > 0)
                        fbWindowInput.StockRemnants = fbWindowInput.StockRemnants
                            .Where(r => !reservedUntilRow.ContainsKey(r.Id)).ToList();

                    var fbVirtualCutouts = PredictWindowCutoutRemnants(
                        fullInput.Rows[startRow], fullInput.Windows, fullInput.Constraints,
                        fbWindowInput.StockRemnants);
                    if (fbVirtualCutouts.Count > 0)
                        fbWindowInput.StockRemnants.AddRange(fbVirtualCutouts);

                    if (fbWindowInput.StockRemnants.Count > _config.MaxStockForSolver)
                    {
                        fbWindowInput.StockRemnants = SelectStockForSolver(
                            fbWindowInput.StockRemnants, fbWindowInput.Rows, _config.MaxStockForSolver, startRow, _constraints);
                    }

                    var fbPatterns = PatternGenerator.GeneratePatterns(fbWindowInput, windowConfig);
                    if (!fbPatterns.Any(kv => kv.Value.Count == 0))
                    {
                        IReadOnlyList<double> fbJoints = startRow > 0
                            ? RowSolution.GetJointPositions(committedRows[startRow - 1]) : null;
                        var fbOptimizer = new OrToolsOptimizer(fbWindowInput, fbPatterns, windowConfig);
                        windowSolution = fbOptimizer.Solve(fbJoints);
                        if (windowSolution.Status != SolverStatus.Optimal && windowSolution.Status != SolverStatus.Feasible && fbJoints != null)
                            windowSolution = new OrToolsOptimizer(fbWindowInput, fbPatterns, windowConfig).Solve(null);
                    }
                }

                if (windowSolution.Status != SolverStatus.Optimal && windowSolution.Status != SolverStatus.Feasible)
                {
                    return new PassResult
                    {
                        IsError = true,
                        ErrorSolution = new OrToolsSolution
                        {
                            FacadeId = fullInput.FacadeId,
                            Status = windowSolution.Status,
                            SolveTimeMs = totalSolveTimeMs,
                            Diagnostics = new List<string>
                            {
                                $"Окно рядов {startRow}..{endRow - 1}: решение не найдено ({windowSolution.Status}).",
                                windowSolution.Diagnostics != null ? string.Join(Environment.NewLine, windowSolution.Diagnostics) : ""
                            }
                        }
                    };
                }

                // Коммитим только первый ряд из окна; следующая итерация сдвигает горизонт на 1.
                var rowToCommit = windowSolution.Rows[0];
                var deferredRows = windowSolution.Rows.Skip(1).Select(r => r.RowIndex).ToList();
                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RollingHorizonEngine] committing row {startRow}" +
                    (deferredRows.Count > 0 ? $", deferred rows [{string.Join(",", deferredRows)}]" : ""));
                committedRows.Add(rowToCommit);

                // === ПОДХОД 2: INTRA-ROW BinPack КОНСОЛИДАЦИЯ ===
                // Группируем мелкие [New] блоки из разных сегментов в минимум физических плит.
                // Запускается ДО счёта плит и ДО UpdateStockAfterRow.
                HashSet<Guid> consolidatedAnchorIds = null;
                HashSet<Guid> consolidatedIntermediateVirtualIds = null;
                List<Remnant> consolidatedVirtualRemnants = null;
                if (EnableIntraRowConsolidate)
                {
                    (_, consolidatedAnchorIds, consolidatedIntermediateVirtualIds, consolidatedVirtualRemnants) =
                        IntraRowConsolidate(rowToCommit, currentStock, fullInput.Constraints);
                    // Добавляем виртуальные остатки в IntraFacadeRemnants для трассировки в Postprocessor
                    allCreatedRemnants.AddRange(consolidatedVirtualRemnants);
                }

                // Считаем новые плиты в этом ряду (ПОСЛЕ консолидации — уже корректно)
                foreach (var seg in rowToCommit.Segments)
                    foreach (var block in seg.Blocks)
                        if (block.Type == BlockType.New)
                            totalNewTiles++;

                // === A2 FIX: Удаляем виртуальные остатки перед реальной вырезкой ===
                int removedVirtual = currentStock.RemoveAll(r => r.Source == "VirtualCutout");
                if (removedVirtual > 0)
                    System.Diagnostics.Debug.WriteLine($"[A2] Row {startRow}: removed {removedVirtual} virtual cutouts, replacing with real ones");

                // === Вырезка Г-элементов (L-Boots) для краевых рядов ===
                var lbootIdsBefore = new HashSet<Guid>(currentStock.Where(r => r.Source == "LBoot_CutOut").Select(r => r.Id));
                ExtractLBootsAndCreateRemnants(rowToCommit, fullInput.Rows[startRow], fullInput.Windows, currentStock, fullInput.Constraints);
                var newLboots = currentStock.Where(r => r.Source == "LBoot_CutOut" && !lbootIdsBefore.Contains(r.Id)).ToList();

                // Обновляем склад
                var created = UpdateStockAfterRow(rowToCommit, currentStock, fullInput.Constraints);
                created.AddRange(newLboots);
                allCreatedRemnants.AddRange(created);
                totalCreatedArea += created.Sum(r => r.Area);

#if DEBUG
                // [ROW-FLOW] Итог создания остатков в ряду
                {
                    var justCreated = created.Where(r => r.Fate != PieceFate.Waste).ToList();
                    if (justCreated.Count > 0)
                        System.Diagnostics.Debug.WriteLine(
                            $"[ROW-FLOW] Row {startRow}: {justCreated.Count} remnants created → next rows: " +
                            string.Join(", ", justCreated.Take(6).Select(r => $"{r.ShortId}({r.Width:F0}×{r.Height:F0})")));
                }
#endif

                // === ПОСТ-ОЧИСТКА КОНСОЛИДАЦИИ ===
                // UpdateStockAfterRow создал width-остатки для anchor-блоков и промежуточные
                // остатки от виртуальной цепочки. Удаляем их — они дублируют виртуальную цепочку.
                // FIX 1.1: Каскадный обход — отслеживаем ВСЕ виртуальные ID (включая транзитивные
                // HeightCut/WidthCut нарезки, создаваемые UpdateStockAfterRow из промежуточных виртуальных).
                if (EnableIntraRowConsolidate && consolidatedAnchorIds != null && consolidatedAnchorIds.Count > 0)
                {
                    // Собираем ВСЕ виртуальные ID (включая каскадные нарезки)
                    var allVirtualRelatedIds = new HashSet<Guid>(consolidatedIntermediateVirtualIds);
                    foreach (var vr in consolidatedVirtualRemnants)
                        allVirtualRelatedIds.Add(vr.Id);

                    var idsToRemove = new HashSet<Guid>();
                    foreach (var r in currentStock)
                    {
                        // Дубль от anchor-блока: UpdateStockAfterRow создал width-остаток 1000мм
                        bool isAnchorWidth = r.SourceBlockId.HasValue &&
                            consolidatedAnchorIds.Contains(r.SourceBlockId.Value) &&
                            r.Source == "RollingHorizon_Width";
                        // Промежуточный виртуальный: UpdateStockAfterRow нарезал виртуальный остаток
                        // FIX 1.1: проверяем allVirtualRelatedIds (полная цепочка) и оба типа нарезок
                        bool isIntermediateCut = r.ParentRemnantId.HasValue &&
                            allVirtualRelatedIds.Contains(r.ParentRemnantId.Value) &&
                            (r.Source == "RollingHorizon_WidthCut" || r.Source == "RollingHorizon_HeightCut");
                        if (isAnchorWidth || isIntermediateCut)
                            idsToRemove.Add(r.Id);
                    }
                    if (idsToRemove.Count > 0)
                    {
                        double removedArea = created.Where(r => idsToRemove.Contains(r.Id)).Sum(r => r.Area);
                        currentStock.RemoveAll(r => idsToRemove.Contains(r.Id));
                        allCreatedRemnants.RemoveAll(r => idsToRemove.Contains(r.Id));
                        totalCreatedArea -= removedArea;
                        System.Diagnostics.Debug.WriteLine($"[CONSOLIDATE] Row {startRow}: removed {idsToRemove.Count} duplicate/intermediate remnants ({removedArea / 1e6:F4} м²)");
                    }
                }

                // Improvement A: reserve cutout remnants from EdgeRows for future EdgeRows
                var currentRowDef = fullInput.Rows[startRow];
                if (currentRowDef.RowType == RowType.BottomEdge || currentRowDef.RowType == RowType.TopEdge)
                {
                    for (int futureRow = startRow + 1; futureRow < totalRows; futureRow++)
                    {
                        var futureRowDef = fullInput.Rows[futureRow];
                        if (futureRowDef.RowType == currentRowDef.RowType)
                        {
                            var cutoutIds = created
                                .Where(r => r.Source != null && (
                                    r.Source.Contains("CutOut") || r.Source.StartsWith("LBoot")))
                                .Select(r => r.Id)
                                .ToList();
                            foreach (var id in cutoutIds)
                            {
                                if (!reservedUntilRow.ContainsKey(id))
                                    reservedUntilRow[id] = futureRow;
                            }
                            if (cutoutIds.Count > 0)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[RESERVE] Row {startRow} ({currentRowDef.RowType}): reserved {cutoutIds.Count} cutout remnants until row {futureRow}");
                            }
                            break;
                        }
                    }
                }

                totalSolveTimeMs += windowSolution.SolveTimeMs;
                startRow++;
            }

            // Суммарная площадь использованных остатков склада: исходный склад − оставшийся склад
            var initialStockIds = new HashSet<Guid>(stockRemnants.Select(r => r.Id));
            double initialStockArea = stockRemnants.Sum(r => r.Area);
            double remainingStockArea = currentStock.Where(r => initialStockIds.Contains(r.Id)).Sum(r => r.Area);
            totalReusedArea = Math.Max(0, initialStockArea - remainingStockArea);

            return new PassResult
            {
                CommittedRows = committedRows,
                CurrentStock = currentStock,
                AllCreatedRemnants = allCreatedRemnants,
                TotalSolveTimeMs = totalSolveTimeMs,
                TotalNewTiles = totalNewTiles,
                TotalCreatedRemnantArea = totalCreatedArea,
                TotalReusedRemnantArea = totalReusedArea,
                IsError = false,
                LBootElements = new List<LBootElement>(_lastLBootElements)
            };
        }

        /// <summary>
        /// Пост-оптимизация: находит ряды с наибольшими отходами (много обрезанных новых плит)
        /// и пытается переоптимизировать их с остатками ТОЛЬКО от предыдущих рядов (строго снизу вверх).
        /// </summary>
        private void PostOptimize(
            FacadeInput fullInput,
            PassResult pass,
            int adaptiveTimeout,
            int adaptiveMaxPat)
        {
            if (pass.CommittedRows.Count == 0) return;

            const int MAX_REOPT_ROWS = 5;
            double tileWidth = fullInput.Constraints.TileWidth;
            const double tol = 0.001;

            // Маппинг: blockId → rowIndex для определения происхождения остатков
            var blockToRow = new Dictionary<Guid, int>();
            for (int ri = 0; ri < pass.CommittedRows.Count; ri++)
                foreach (var seg in pass.CommittedRows[ri].Segments)
                    foreach (var b in seg.Blocks)
                        blockToRow[b.Id] = ri;

            var initialStockIds = new HashSet<Guid>(fullInput.StockRemnants.Select(r => r.Id));

            // 1. Оцениваем "потери" каждого ряда: количество обрезанных новых плит
            // FIX 1.2: Пропускаем ряды, прошедшие IntraRowConsolidate (содержат CutRemnant
            // блоки с виртуальными остатками IntraRow_Virtual). Переоптимизация таких рядов
            // приведёт к потере виртуальных остатков и нарушению материального баланса (C14).
            var consolidatedRowIds = new HashSet<int>();
            foreach (var cr in pass.AllCreatedRemnants.Where(r => r.Source == "IntraRow_Virtual"))
            {
                if (cr.CreatedAtRow >= 0)
                    consolidatedRowIds.Add(cr.CreatedAtRow);
            }

            var rowWaste = new List<(int rowIdx, int wasteCount, int newTiles)>();
            for (int i = 0; i < pass.CommittedRows.Count; i++)
            {
                // FIX 1.2: пропускаем ряды с IntraRowConsolidate
                if (consolidatedRowIds.Contains(i))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[PostOptimize] Row {i}: SKIP (IntraRowConsolidate applied)");
                    continue;
                }

                var row = pass.CommittedRows[i];
                int waste = 0;
                int newTiles = 0;
                foreach (var seg in row.Segments)
                {
                    foreach (var b in seg.Blocks)
                    {
                        if (b.Type == BlockType.New)
                        {
                            newTiles++;
                            if (b.Width < tileWidth - tol)
                                waste++;
                        }
                    }
                }
                if (waste >= 1 && pass.CurrentStock.Count > 0)
                    rowWaste.Add((i, waste, newTiles));
            }

            if (rowWaste.Count == 0) return;

            // Берём top по кол-ву обрезанных плит, затем сортируем по rowIdx (снизу вверх)
            var candidates = rowWaste
                .OrderByDescending(x => x.wasteCount)
                .ThenByDescending(x => x.newTiles)
                .Take(MAX_REOPT_ROWS)
                .OrderBy(x => x.rowIdx)
                .ToList();

            foreach (var candidate in candidates)
            {
                int idx = candidate.rowIdx;
                var oldRow = pass.CommittedRows[idx];
                var rowDef = fullInput.Rows[idx];

                // Собираем стыки соседних рядов для ограничения перевязки
                IReadOnlyList<double> prevJoints = idx > 0
                    ? RowSolution.GetJointPositions(pass.CommittedRows[idx - 1]) : null;
                IReadOnlyList<double> nextJoints = idx < pass.CommittedRows.Count - 1
                    ? RowSolution.GetJointPositions(pass.CommittedRows[idx + 1]) : null;

                // Фильтрация склада: только начальный склад + остатки от рядов НИЖЕ текущего
                var eligibleStock = pass.CurrentStock.Where(r =>
                {
                    if (initialStockIds.Contains(r.Id)) return true;
                    if (!r.SourceBlockId.HasValue) return true;
                    if (blockToRow.TryGetValue(r.SourceBlockId.Value, out int srcRow))
                        return srcRow < idx;
                    return false;
                }).ToList();

                if (eligibleStock.Count == 0) continue;

                var singleRowInput = new FacadeInput
                {
                    FacadeId = fullInput.FacadeId,
                    Width = fullInput.Width,
                    Height = fullInput.Height,
                    MinX = fullInput.MinX,
                    MinY = fullInput.MinY,
                    TrueFacadeAreaM2 = fullInput.TrueFacadeAreaM2,
                    TrueWindowsAreaM2 = fullInput.TrueWindowsAreaM2,
                    Rows = new List<RowDefinition> { rowDef },
                    Windows = fullInput.Windows,
                    StockRemnants = new List<Remnant>(eligibleStock),
                    Constraints = fullInput.Constraints,
                    FirstRowWithRelease = fullInput.FirstRowWithRelease
                };

                int reoptMaxRemnantWeight = _config.MaxRemnantUsageWeight > 0 ? _config.MaxRemnantUsageWeight : 135;
                var reoptConfig = new OptimizerConfig
                {
                    TimeoutSeconds = 2,
                    LogProgress = false,
                    MaxPatternsPerSegment = adaptiveMaxPat,
                    TileWeight = _config.TileWeight > 0 ? _config.TileWeight : 10_000,
                    RemnantUsageWeight = reoptMaxRemnantWeight,
                    MaxRemnantUsageWeight = reoptMaxRemnantWeight,
                    ImprovementCMinDim = _config.ImprovementCMinDim
                };

                var patterns = PatternGenerator.GeneratePatterns(singleRowInput, reoptConfig);
                if (patterns.Values.Any(v => v.Count == 0)) continue;

                // Фильтруем паттерны по перевязке с соседями
                if (prevJoints != null || nextJoints != null)
                {
                    double minOffset = fullInput.Constraints.MinJointOffset;
                    foreach (var key in patterns.Keys.ToList())
                    {
                        patterns[key] = patterns[key].Where(p =>
                        {
                            if (prevJoints != null)
                            {
                                foreach (var j1 in p.JointPositions)
                                    foreach (var j2 in prevJoints)
                                        if (Math.Abs(j1 - j2) < minOffset - tol) return false;
                            }
                            if (nextJoints != null)
                            {
                                foreach (var j1 in p.JointPositions)
                                    foreach (var j2 in nextJoints)
                                        if (Math.Abs(j1 - j2) < minOffset - tol) return false;
                            }
                            return true;
                        }).ToList();
                    }
                    if (patterns.Values.Any(v => v.Count == 0)) continue;
                }

                var optimizer = new OrToolsOptimizer(singleRowInput, patterns, reoptConfig);
                var sol = optimizer.Solve(null);

                if (sol.Status != SolverStatus.Optimal && sol.Status != SolverStatus.Feasible)
                    continue;

                // Считаем новые плиты нового решения
                int newNewTiles = 0;
                foreach (var seg in sol.Rows[0].Segments)
                    foreach (var b in seg.Blocks)
                        if (b.Type == BlockType.New)
                            newNewTiles++;

                // Принимаем только если стало лучше
                if (newNewTiles < candidate.newTiles)
                {
                    // === Шаг 0: Собираем ID блоков старого ряда ===
                    var oldBlockIds = new HashSet<Guid>();
                    foreach (var seg in oldRow.Segments)
                        foreach (var b in seg.Blocks)
                            oldBlockIds.Add(b.Id);

                    // === Шаг 1: Удаляем остатки, СОЗДАННЫЕ старым рядом ===
                    pass.CurrentStock.RemoveAll(r =>
                        r.SourceBlockId.HasValue && oldBlockIds.Contains(r.SourceBlockId.Value));
                    pass.AllCreatedRemnants.RemoveAll(r =>
                        r.SourceBlockId.HasValue && oldBlockIds.Contains(r.SourceBlockId.Value));

                    // FIX 1.3: Удаляем LBoot_CutOut остатки от старого ряда ПЕРЕД повторной вырезкой.
                    // Без этого остатки от старой вырезки остаются в складе как фантомные.
                    int removedLbootCount = pass.CurrentStock.RemoveAll(r =>
                        r.Source == "LBoot_CutOut" && r.CreatedAtRow == rowDef.RowIndex);
                    if (removedLbootCount > 0)
                    {
                        pass.AllCreatedRemnants.RemoveAll(r =>
                            r.Source == "LBoot_CutOut" && r.CreatedAtRow == rowDef.RowIndex);
                        System.Diagnostics.Debug.WriteLine(
                            $"[PostOptimize] Row {idx}: removed {removedLbootCount} old LBoot_CutOut remnants before re-extraction");
                    }

                    pass.LBootElements.RemoveAll(lb => lb.RowIndex == rowDef.RowIndex);

                    // === Шаг 2: Возвращаем остатки, ИСПОЛЬЗОВАННЫЕ старым рядом, обратно на склад ===
                    foreach (var seg in oldRow.Segments)
                    {
                        foreach (var b in seg.Blocks)
                        {
                            if (!b.RemnantId.HasValue) continue;
                            if (pass.CurrentStock.Any(r => r.Id == b.RemnantId.Value)) continue;

                            var original = fullInput.StockRemnants.FirstOrDefault(r => r.Id == b.RemnantId.Value);
                            if (original != null)
                            {
                                pass.CurrentStock.Add(original);
                                continue;
                            }
                            var fromOther = pass.AllCreatedRemnants.FirstOrDefault(r => r.Id == b.RemnantId.Value);
                            if (fromOther != null)
                                pass.CurrentStock.Add(fromOther);
                        }
                    }

                    // === Шаг 3: Вырезка Г-элементов для нового ряда ===
                    var lbootIdsBeforeReopt = new HashSet<Guid>(pass.CurrentStock.Where(r => r.Source == "LBoot_CutOut").Select(r => r.Id));
                    int lbootBefore = _lastLBootElements.Count;
                    ExtractLBootsAndCreateRemnants(
                        sol.Rows[0], rowDef, fullInput.Windows,
                        pass.CurrentStock, fullInput.Constraints);
                    for (int li = lbootBefore; li < _lastLBootElements.Count; li++)
                        pass.LBootElements.Add(_lastLBootElements[li]);
                    var newLbootsReopt = pass.CurrentStock.Where(r => r.Source == "LBoot_CutOut" && !lbootIdsBeforeReopt.Contains(r.Id)).ToList();

                    // === Шаг 4: Создаём остатки от нового ряда + удаляем использованные ===
                    var created = UpdateStockAfterRow(
                        sol.Rows[0], pass.CurrentStock, fullInput.Constraints);
                    created.AddRange(newLbootsReopt);
                    pass.AllCreatedRemnants.AddRange(created);

                    // === Шаг 5: Обновляем blockToRow для новых блоков ===
                    foreach (var id in oldBlockIds)
                        blockToRow.Remove(id);
                    foreach (var seg in sol.Rows[0].Segments)
                        foreach (var b in seg.Blocks)
                            blockToRow[b.Id] = idx;

                    // === Шаг 6: Заменяем ряд ===
                    pass.CommittedRows[idx] = sol.Rows[0];
                    pass.TotalNewTiles += (newNewTiles - candidate.newTiles);
                    pass.TotalSolveTimeMs += sol.SolveTimeMs;
                }
            }
        }

        /// <summary>
        /// Строит FacadeInput только для рядов [startRow, endRow) с текущим складом.
        /// </summary>
        private static FacadeInput BuildWindowInput(
            FacadeInput fullInput,
            int startRow,
            int endRow,
            List<Remnant> currentStock)
        {
            var windowRows = fullInput.Rows
                .Where(r => r.RowIndex >= startRow && r.RowIndex < endRow)
                .ToList();

            return new FacadeInput
            {
                FacadeId = fullInput.FacadeId,
                Width = fullInput.Width,
                Height = fullInput.Height,
                MinX = fullInput.MinX,
                MinY = fullInput.MinY,
                TrueFacadeAreaM2 = fullInput.TrueFacadeAreaM2,
                TrueWindowsAreaM2 = fullInput.TrueWindowsAreaM2,
                Rows = windowRows,
                Windows = fullInput.Windows,
                StockRemnants = new List<Remnant>(currentStock),
                Constraints = fullInput.Constraints,
                FirstRowWithRelease = fullInput.FirstRowWithRelease
            };
        }

        /// <summary>
        /// Обновляет текущий склад после укладки одного ряда: убрать использованные остатки, добавить созданные.
        /// Возвращает список всех созданных остатков (для полного учёта в отчёте).
        /// </summary>
        /// <summary>
        /// Обрабатывает блоки ряда в контексте оконных проёмов (ТЗ v13.1 §29):
        /// - EDGE-ряды (BottomEdge/TopEdge): вырезка Г-элементов через Boolean Subtraction
        /// - MIDDLE-ряды: обрезка/удаление блоков, попавших в зону проёма
        /// Знаковая конвенция: нахлёст δ ВНУТРЬ проёма → effLeft = MinX − δ, effRight = MaxX + δ
        /// </summary>
        private void ExtractLBootsAndCreateRemnants(RowSolution rowSol, RowDefinition rowDef, List<WindowInfo> windows, List<Remnant> currentStock, OptimizationConstraints constraints)
        {
            const double tol = 0.001;
            double lBootMinShelf = 150.0; // ТЗ v13.1 §6: LBootMinShelf

            foreach (var w in windows)
            {
                // ТЗ v13.3: Эффективный проём — нахлёст ВНУТРЬ окна (сужает пустую зону)
                double effLeft = w.MinX + constraints.WindowOverlap;    // утеплитель заходит на 20 мм вправо за раму
                double effRight = w.MaxX - constraints.WindowOverlap;   // утеплитель заходит на 20 мм влево за раму
                double effBottom = w.MinY + constraints.WindowOverlap;  // утеплитель заходит на 20 мм вверх за раму
                double effTop = w.MaxY - constraints.WindowOverlap;     // утеплитель заходит на 20 мм вниз за раму

                bool isEdgeRow = rowDef.RowType == RowType.BottomEdge || rowDef.RowType == RowType.TopEdge;
                bool isMiddleRow = rowDef.RowType == RowType.Middle;

                // Также проверяем старым способом для совместимости (если RowType ещё не проставлен)
                if (!isEdgeRow && !isMiddleRow)
                {
                    isEdgeRow = Preprocessor.IsTopOrBottomRowOfWindow(rowDef, w);
                    if (!isEdgeRow)
                    {
                        // Проверка Middle: ряд полностью внутри окна
                        bool rowInside = rowDef.Y >= w.MinY - tol && (rowDef.Y + rowDef.Height) <= w.MaxY + tol;
                        isMiddleRow = rowInside;
                    }
                }

                if (!isEdgeRow && !isMiddleRow) continue;

                foreach (var seg in rowSol.Segments)
                {
                    // Работаем с копией блоков, чтобы безопасно модифицировать
                    var newBlocks = new List<Block>();
                    bool modified = false;

                    foreach (var block in seg.Blocks)
                    {
                        double bLeft = block.X;
                        double bRight = block.X + block.Width;

                        // Проверяем пересечение блока с эффективной зоной проёма по X
                        bool overlapsWindow = bRight > effLeft + tol && bLeft < effRight - tol;

                        if (!overlapsWindow)
                        {
                            // Блок вне зоны окна — оставляем как есть
                            newBlocks.Add(block);
                            continue;
                        }

                        modified = true;

                        if (isMiddleRow)
                        {
                            // ТЗ v13.1 §29.4: MIDDLE-ряды — зона проёма ПУСТАЯ
                            // Блок полностью внутри проёма → удалить, сохранить как остаток
                            if (bLeft >= effLeft - tol && bRight <= effRight + tol)
                            {
                                // Весь блок внутри → выбрасываем, сохраняем остаток
                                SaveCutoutAsRemnant(block.Width, rowSol.Height, currentStock, constraints, block.Id, rowDef.RowIndex);
                                continue; // не добавляем в newBlocks
                            }

                            // Блок частично слева от окна
                            if (bLeft < effLeft - tol && bRight > effLeft + tol)
                            {
                                double keepWidth = effLeft - bLeft;
                                if (keepWidth >= constraints.MinBlockNearWindow)
                                {
                                    newBlocks.Add(new Block
                                    {
                                        X = bLeft,
                                        Width = keepWidth,
                                        Type = block.Type,
                                        RemnantId = block.RemnantId,
                                        CutFrom = block.CutFrom,
                                        IsRotated = block.IsRotated
                                    });
                                }
                                // Вырезанная часть → остаток
                                double cutWidth = bRight - effLeft;
                                SaveCutoutAsRemnant(cutWidth, rowSol.Height, currentStock, constraints, block.Id, rowDef.RowIndex);
                                continue;
                            }

                            // Блок частично справа от окна
                            if (bLeft < effRight - tol && bRight > effRight + tol)
                            {
                                double keepWidth = bRight - effRight;
                                if (keepWidth >= constraints.MinBlockNearWindow)
                                {
                                    newBlocks.Add(new Block
                                    {
                                        X = effRight,
                                        Width = keepWidth,
                                        Type = block.Type,
                                        RemnantId = block.RemnantId,
                                        CutFrom = block.CutFrom,
                                        IsRotated = block.IsRotated
                                    });
                                }
                                double cutWidth = effRight - bLeft;
                                SaveCutoutAsRemnant(cutWidth, rowSol.Height, currentStock, constraints, block.Id, rowDef.RowIndex);
                                continue;
                            }

                            // Блок перекрывает окно полностью → разрезаем на 2 части
                            if (bLeft < effLeft - tol && bRight > effRight + tol)
                            {
                                double leftPart = effLeft - bLeft;
                                double rightPart = bRight - effRight;

                                if (leftPart >= constraints.MinBlockNearWindow)
                                {
                                    newBlocks.Add(new Block { X = bLeft, Width = leftPart, Type = block.Type,
                                        RemnantId = block.RemnantId, CutFrom = block.CutFrom, IsRotated = block.IsRotated });
                                }
                                if (rightPart >= constraints.MinBlockNearWindow)
                                {
                                    newBlocks.Add(new Block { X = effRight, Width = rightPart, Type = block.Type });
                                }
                                double cutWidth = effRight - effLeft;
                                SaveCutoutAsRemnant(cutWidth, rowSol.Height, currentStock, constraints, block.Id, rowDef.RowIndex);
                                continue;
                            }
                        }
                        else if (isEdgeRow)
                        {
                            // ТЗ v13.1 §29.3: EDGE-ряды — Г-элементы через Boolean Subtraction
                            // Вычисляем зону пересечения блока с проёмом
                            double cutX1 = Math.Max(bLeft, effLeft);
                            double cutX2 = Math.Min(bRight, effRight);
                            double cutY1 = Math.Max(rowSol.Y, effBottom);
                            double cutY2 = Math.Min(rowSol.Y + rowSol.Height, effTop);

                            double cutWidth = cutX2 - cutX1;
                            double cutHeight = cutY2 - cutY1;

                            if (cutWidth < tol || cutHeight < tol)
                            {
                                newBlocks.Add(block);
                                continue;
                            }

                            // Вычисляем полки L-boot
                            double shelfH = block.Width - cutWidth;  // горизонтальная полка (ширина оставшейся части)
                            double shelfV = rowSol.Height - cutHeight; // вертикальная полка (высота оставшейся части)

                            // ТЗ v13.3 §14.1: Строгий запрет на сдвиг границ выреза (wkeff).
                            // Границы cut X и Y константны. Если полка мала, валидатор зафиксирует ошибку E_LBOOT_SHELF_SIZE.


                            shelfH = block.Width - cutWidth;
                            shelfV = rowSol.Height - cutHeight;

                            // Вырезка → остаток на склад
                            SaveCutoutAsRemnant(cutWidth, cutHeight, currentStock, constraints, block.Id, rowDef.RowIndex);

                            // Определяем угол L-boot
                            string corner = DetectCorner(block.X, block.Width, rowSol.Y, rowSol.Height, w);

                            // Записываем LBootElement для трассируемости (C16a)
                            _lastLBootElements.Add(new LBootElement
                            {
                                WindowId = w.Id,
                                Corner = corner,
                                RowIndex = rowDef.RowIndex,
                                SourceBlockX = block.X,
                                SourceBlockWidth = block.Width,
                                ShelfH = shelfH,
                                ShelfV = shelfV,
                                CutoutWidth = cutWidth,
                                CutoutHeight = cutHeight
                            });

                            // Блок остаётся в раскладке (L-образная форма отрисуется в Postprocessor)
                            newBlocks.Add(block);
                        }
                    }

                    if (modified)
                    {
                        seg.Blocks = newBlocks;
                    }
                }
            }
        }

        /// <summary>Сохраняет вырезанный прямоугольник как остаток, если он достаточно большой.</summary>
        private static void SaveCutoutAsRemnant(double width, double height, List<Remnant> stock, OptimizationConstraints constraints, Guid? sourceBlockId = null, int createdAtRow = -1)
        {
            if ((width >= constraints.MinBlock && height >= constraints.MinRemnantToSave) ||
                (height >= constraints.MinBlock && width >= constraints.MinRemnantToSave))
            {
                var r = new Remnant
                {
                    Id = Guid.NewGuid(),
                    Width = width,
                    Height = height,
                    Source = "LBoot_CutOut",
                    SourceBlockId = sourceBlockId,
                    CreatedAtRow = createdAtRow
                };
                stock.Add(r);
                if (createdAtRow >= 0)
                    System.Diagnostics.Debug.WriteLine($"[ROW-FLOW] Created {r.Width:F0}×{r.Height:F0} (ShortId={r.ShortId}) at row {createdAtRow} src={r.Source}");
            }
        }

        /// <summary>
        /// Выбирает остатки для передачи солверу с ограничением maxStock.
        /// Стратегия: VirtualCutout всегда включаются + топ-N/2 по площади + топ-N/2 по MatchQuality
        /// к сегментам текущего ряда. Дедупликация по GUID.
        /// Это предотвращает ситуацию, когда идеально подходящий маленький остаток
        /// вытесняется крупными остатками с плохим соответствием.
        /// </summary>
        /// <summary>
        /// Глобальный greedy pre-pass: присваивает каждому остатку склада "предпочтительный" ряд
        /// на основе MatchQuality к сегментам этого ряда.
        /// Возвращает Dictionary&lt;RemnantId, RowIndex&gt; — подсказки для CP-SAT objective.
        /// Не является hard constraint — только soft hint (penalty в objective).
        /// Включается через config.EnableRemnantPreAllocation.
        /// </summary>
        private static Dictionary<Guid, int> PreAllocateRemnants(
            FacadeInput fullInput, List<Remnant> stockRemnants)
        {
            var hints = new Dictionary<Guid, int>();
            if (stockRemnants.Count == 0 || fullInput.Rows.Count == 0)
                return hints;

            foreach (var remnant in stockRemnants)
            {
                if (remnant.Source == "VirtualCutout") continue;

                double bestQuality = 0;
                int bestRow = fullInput.Rows[0].RowIndex;

                foreach (var row in fullInput.Rows)
                {
                    foreach (var seg in row.Segments)
                    {
                        double segArea = seg.Width * row.Height;
                        if (segArea <= 0) continue;

                        // Прямая ориентация
                        double usableW = Math.Min(remnant.Width, seg.Width);
                        double usableH = Math.Min(remnant.Height, row.Height);
                        double q = (usableW * usableH) / segArea;

                        // Ротированная ориентация
                        double usableWr = Math.Min(remnant.Height, seg.Width);
                        double usableHr = Math.Min(remnant.Width, row.Height);
                        double qr = (usableWr * usableHr) / segArea;

                        double bestQ = Math.Max(q, qr);
                        if (bestQ > bestQuality)
                        {
                            bestQuality = bestQ;
                            bestRow = row.RowIndex;
                        }
                    }
                }

                hints[remnant.Id] = bestRow;
                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RollingHorizonEngine] PreAllocate: remnant {remnant.Id} " +
                    $"({remnant.Width:F0}×{remnant.Height:F0}) → row {bestRow}, matchQuality={bestQuality:F3}");
            }

            int rowCount = hints.Values.Distinct().Count();
            System.Diagnostics.Debug.WriteLine(
                $"INFO [RollingHorizonEngine] PreAllocation: {hints.Count} remnants hinted across {rowCount} rows");

            return hints;
        }

        private static List<Remnant> SelectStockForSolver(
            List<Remnant> allStock, List<RowDefinition> rows, int maxStock, int rowIndex,
            OptimizationConstraints? constraints = null)
        {
            var virtualOnes = allStock.Where(r => r.Source == "VirtualCutout").ToList();
            var nonVirtual = allStock.Where(r => r.Source != "VirtualCutout").ToList();

            var currentRow = rows.FirstOrDefault(r => r.RowIndex == rowIndex);
            double rowHeight = currentRow?.Height ?? 600.0;

            // === GEOMETRIC FILTERING (Phase 2 Task 4a) ===
            var feasibleNonVirtual = nonVirtual
                .Where(r => constraints != null ? PatternGenerator.CanUseRemnant(r, rowHeight, constraints) : true)
                .ToList();

            int budget = maxStock - virtualOnes.Count;
            var result = new List<Remnant>(virtualOnes);

            if (budget <= 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RollingHorizonEngine] Row {rowIndex}: stock cap — only virtual cutouts ({virtualOnes.Count})");
            }
            else if (feasibleNonVirtual.Count <= budget)
            {
                result.AddRange(feasibleNonVirtual);
            }
            else
            {
                // B3 FIX: Гибридный отбор вместо чисто по площади.
                // 50% бюджета — крупнейшие по площади (очистка склада от больших кусков),
                // 50% бюджета — лучшие по match-score к текущему ряду (точное попадание в размер).
                // Дедупликация: если остаток попал в обе группы, место освобождается.
                int halfBudget = budget / 2;
                int otherHalf = budget - halfBudget;

                // Группа 1: top по площади
                var topByArea = feasibleNonVirtual
                    .OrderByDescending(r => r.Area)
                    .Take(halfBudget)
                    .ToList();
                var selectedIds = new HashSet<Guid>(topByArea.Select(r => r.Id));

                // Группа 2: top по match-score (насколько хорошо остаток подходит к текущему ряду)
                // Match-score = min(remnantHeight, rowHeight) / max(remnantHeight, rowHeight)
                //             × min(remnantWidth, segmentAvgWidth) / max(remnantWidth, segmentAvgWidth)
                // Высокий score = остаток хорошо вписывается по габаритам.
                double avgSegWidth = currentRow?.Segments.Count > 0
                    ? currentRow.Segments.Average(s => s.Width)
                    : 1200.0;

                var topByMatch = feasibleNonVirtual
                    .Where(r => !selectedIds.Contains(r.Id))
                    .Select(r =>
                    {
                        double heightFit = Math.Min(r.Height, rowHeight) / Math.Max(r.Height, rowHeight);
                        double widthFit = Math.Min(r.Width, avgSegWidth) / Math.Max(r.Width, avgSegWidth);
                        double matchScore = heightFit * widthFit;
                        return (Remnant: r, MatchScore: matchScore);
                    })
                    .OrderByDescending(x => x.MatchScore)
                    .Take(otherHalf)
                    .Select(x => x.Remnant)
                    .ToList();

                result.AddRange(topByArea);
                result.AddRange(topByMatch);

                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RollingHorizonEngine] Row {rowIndex}: B3 hybrid selection: " +
                    $"{topByArea.Count} by area + {topByMatch.Count} by match-score = {topByArea.Count + topByMatch.Count} total");
            }

            int dropped = allStock.Count - result.Count;
            int droppedByFilter = nonVirtual.Count - feasibleNonVirtual.Count;
            
            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [RollingHorizonEngine] Row {rowIndex}: stock selected: " +
                $"{result.Count} (dropped {dropped}) — Geometric Filter Active, Filtered out {droppedByFilter}");

            Console.WriteLine(
                $"[STOCK-FILTER] Row {rowIndex}: Total stock={allStock.Count}, " +
                $"Feasible={feasibleNonVirtual.Count}, Given to solver={result.Count}");

            return result;
        }

        /// <summary>
        /// A2 FIX: Предсказывает остатки, которые будут созданы при вырезке оконных проёмов в данном ряду.
        /// Вызывается ПЕРЕД солвером, чтобы эти «виртуальные» остатки были доступны для использования
        /// в других сегментах того же ряда (ТЗ §9, C14 — немедленное обновление склада).
        /// 
        /// Логика предсказания:
        /// - MIDDLE ряды: каждое окно «съедает» N целых блоков (TileWidth × rowHeight) → они становятся остатками.
        /// - EDGE ряды: для каждого окна вычисляем cutWidth × cutHeight → остаток от L-boot вырезки.
        /// </summary>
        private static List<Remnant> PredictWindowCutoutRemnants(
            RowDefinition rowDef,
            List<WindowInfo> windows,
            OptimizationConstraints constraints,
            List<Remnant> currentStock = null)
        {
            var predicted = new List<Remnant>();
            if (windows == null || windows.Count == 0) return predicted;

            const double tol = 0.001;
            double rowMinY = rowDef.Y;
            double rowMaxY = rowDef.Y + rowDef.Height;

            foreach (var w in windows)
            {
                // Классифицируем ряд относительно этого окна
                bool isEdgeRow = false;
                bool isMiddleRow = false;

                if (rowDef.RowType == RowType.BottomEdge || rowDef.RowType == RowType.TopEdge)
                {
                    // Проверяем что именно это окно вызывает edge
                    bool isBottomEdge = w.MinY >= rowMinY - tol && w.MinY < rowMaxY - tol;
                    bool isTopEdge = w.MaxY > rowMinY + tol && w.MaxY <= rowMaxY + tol;
                    isEdgeRow = isBottomEdge || isTopEdge;
                }
                else if (rowDef.RowType == RowType.Middle)
                {
                    bool rowInside = rowMinY >= w.MinY - tol && rowMaxY <= w.MaxY + tol;
                    isMiddleRow = rowInside;
                }
                else
                {
                    // Fallback: классификация вручную
                    bool isBottomEdge = w.MinY >= rowMinY - tol && w.MinY < rowMaxY - tol;
                    bool isTopEdge = w.MaxY > rowMinY + tol && w.MaxY <= rowMaxY + tol;
                    isEdgeRow = isBottomEdge || isTopEdge;
                    if (!isEdgeRow)
                    {
                        bool rowInside = rowMinY >= w.MinY - tol && rowMaxY <= w.MaxY + tol;
                        isMiddleRow = rowInside;
                    }
                }

                if (!isEdgeRow && !isMiddleRow) continue;

                // Эффективные границы окна (нахлёст ВНУТРЬ)
                double effLeft = w.MinX + constraints.WindowOverlap;
                double effRight = w.MaxX - constraints.WindowOverlap;
                double effBottom = w.MinY + constraints.WindowOverlap;
                double effTop = w.MaxY - constraints.WindowOverlap;

                double windowEffWidth = effRight - effLeft;
                if (windowEffWidth < constraints.MinBlock) continue;

                if (isMiddleRow)
                {
                    // MIDDLE: блоки внутри окна полностью вырезаются.
                    // Предсказываем сколько целых блоков попадает в зону окна.
                    // Типичный блок = TileWidth (1200мм), остаток = TileWidth × rowHeight
                    // Минимально 1 блок, максимально — ширина окна / TileWidth + 1
                    int estimatedBlocks = Math.Max(1, (int)Math.Ceiling(windowEffWidth / constraints.TileWidth));
                    for (int i = 0; i < estimatedBlocks; i++)
                    {
                        // Каждый целый блок, попавший в окно, даёт остаток
                        double remnantWidth = constraints.TileWidth;
                        double remnantHeight = rowDef.Height;

                        // Последний блок может быть частичным
                        double remaining = windowEffWidth - i * constraints.TileWidth;
                        if (remaining < constraints.TileWidth)
                            remnantWidth = remaining;

                        // Проверяем минимальные размеры (как в SaveCutoutAsRemnant)
                        if ((remnantWidth >= constraints.MinBlock && remnantHeight >= constraints.MinRemnantToSave) ||
                            (remnantHeight >= constraints.MinBlock && remnantWidth >= constraints.MinRemnantToSave))
                        {
                            predicted.Add(new Remnant
                            {
                                Id = Guid.NewGuid(),
                                Width = remnantWidth,
                                Height = remnantHeight,
                                Source = "VirtualCutout"
                            });
                        }
                    }
                }
                else if (isEdgeRow)
                {
                    // EDGE: L-boot вырезка. Вырезанная часть = cutWidth × cutHeight
                    double cutWidth = windowEffWidth;
                    double cutHeight;

                    // Определяем высоту вырезки в зависимости от того, bottom или top edge
                    bool isBottomEdge = w.MinY >= rowMinY - tol && w.MinY < rowMaxY - tol;
                    if (isBottomEdge)
                    {
                        // Вырезка от нижней границы окна до верха ряда
                        cutHeight = rowMaxY - effBottom;
                    }
                    else
                    {
                        // Вырезка от низа ряда до верхней границы окна
                        cutHeight = effTop - rowMinY;
                    }

                    if (cutHeight < tol) continue;

                    // Аналог SaveCutoutAsRemnant — один остаток на всю зону вырезки
                    if ((cutWidth >= constraints.MinBlock && cutHeight >= constraints.MinRemnantToSave) ||
                        (cutHeight >= constraints.MinBlock && cutWidth >= constraints.MinRemnantToSave))
                    {
                        predicted.Add(new Remnant
                        {
                            Id = Guid.NewGuid(),
                            Width = cutWidth,
                            Height = cutHeight,
                            Source = "VirtualCutout"
                        });
                    }
                }
            }

            // Improvement B: if matching stock remnants exist, skip virtual cutouts
            if (currentStock != null && currentStock.Count > 0 && predicted.Count > 0)
            {
                const double sizeTol = 50.0;
                var nonVirtualStock = currentStock.Where(r => r.Source != "VirtualCutout").ToList();
                var kept = new List<Remnant>();
                var usedStockIds = new HashSet<Guid>();

                foreach (var vc in predicted)
                {
                    var matching = nonVirtualStock.FirstOrDefault(s =>
                        !usedStockIds.Contains(s.Id) &&
                        ((Math.Abs(s.Width - vc.Width) < sizeTol && Math.Abs(s.Height - vc.Height) < sizeTol) ||
                         (Math.Abs(s.Width - vc.Height) < sizeTol && Math.Abs(s.Height - vc.Width) < sizeTol)));

                    if (matching != null)
                    {
                        usedStockIds.Add(matching.Id);
                        System.Diagnostics.Debug.WriteLine(
                            $"[IMPROVE-B] Skipping virtual cutout {vc.Width:F0}x{vc.Height:F0} — " +
                            $"stock remnant {matching.Width:F0}x{matching.Height:F0} already available");
                    }
                    else
                    {
                        kept.Add(vc);
                    }
                }

                predicted = kept;
            }

            return predicted;
        }

        /// <summary>Определяет угол L-boot элемента (BL/BR/TL/TR) по позиции блока и окна.</summary>
        private static string DetectCorner(double blockX, double blockWidth, double rowY, double rowHeight, WindowInfo w)
        {
            bool isLeft = blockX < w.MinX;
            bool isBottom = rowY <= w.MinY;
            if (isBottom) return isLeft ? "BL" : "BR";
            return isLeft ? "TL" : "TR";
        }

        /// <summary>
        /// Intra-Row BinPack консолидация (Подход 2).
        /// Собирает все частичные [New W &lt; TileWidth] блоки из всех сегментов ряда,
        /// упаковывает их алгоритмом BFD в минимум физических плит 1200 мм.
        /// Для каждого бина с >1 блоком: anchor-блок остаётся [New], остальные
        /// конвертируются в [CutRemnant] с цепочкой виртуальных остатков.
        /// Перед конвертацией каждого блока выполняется проверка C13 (MaxConsecutiveRemnants).
        /// Блоки, конвертация которых нарушала бы C13, остаются [New].
        /// </summary>
        /// <returns>
        /// (savedTiles, anchorIds, intermediateVirtualIds, allVirtualRemnants)
        /// </returns>
        private static (int savedTiles, HashSet<Guid> anchorIds, HashSet<Guid> intermediateVirtualIds, List<Remnant> allVirtualRemnants)
            IntraRowConsolidate(
                RowSolution row,
                List<Remnant> currentStock,
                OptimizationConstraints constraints)
        {
            const double tol = 0.001;
            double tileWidth = constraints.TileWidth;
            int maxConsec = constraints.MaxConsecutiveRemnants;

            // Строим быстрый lookup: BlockId → SegmentSolution
            var blockToSeg = new Dictionary<Guid, SegmentSolution>();
            foreach (var seg in row.Segments)
                foreach (var block in seg.Blocks)
                    blockToSeg[block.Id] = seg;

            // Собираем все частичные [New] блоки (ширина < 1200)
            var partialNew = new List<Block>();
            foreach (var seg in row.Segments)
                foreach (var block in seg.Blocks)
                    if (block.Type == BlockType.New && block.Width < tileWidth - tol)
                        partialNew.Add(block);

            var anchorIds = new HashSet<Guid>();
            var intermediateVirtualIds = new HashSet<Guid>();
            var allVirtualRemnants = new List<Remnant>();

            if (partialNew.Count <= 1)
                return (0, anchorIds, intermediateVirtualIds, allVirtualRemnants);

            // BFD: сортируем по убыванию ширины — крупные блоки получают лучшие позиции
            partialNew.Sort((a, b) => b.Width.CompareTo(a.Width));

            // Best Fit Decreasing бин-пакинг в бины по 1200 мм
            var bins = new List<List<Block>>();
            var binRemaining = new List<double>();

            foreach (var block in partialNew)
            {
                int bestBin = -1;
                double bestRemaining = double.MaxValue;
                for (int i = 0; i < bins.Count; i++)
                {
                    if (binRemaining[i] >= block.Width - tol && binRemaining[i] < bestRemaining)
                    {
                        bestBin = i;
                        bestRemaining = binRemaining[i];
                    }
                }

                if (bestBin == -1)
                {
                    bins.Add(new List<Block> { block });
                    binRemaining.Add(tileWidth - block.Width);
                }
                else
                {
                    bins[bestBin].Add(block);
                    binRemaining[bestBin] -= block.Width;
                }
            }

            int totalConverted = 0;

            foreach (var bin in bins)
            {
                if (bin.Count <= 1)
                    continue;

                var anchorBlock = bin[0];
                double currentVirtualWidth = tileWidth - anchorBlock.Width;
                bool anyConverted = false;
                var binVirtualRemnants = new List<Remnant>();

                for (int i = 1; i < bin.Count; i++)
                {
                    var blockToConvert = bin[i];

                    // C13 проверка: не создаём >MaxConsecutiveRemnants подряд идущих [Remnant/CutRemnant]
                    if (blockToSeg.TryGetValue(blockToConvert.Id, out var blockSeg))
                    {
                        int consecBefore = 0, consecAfter = 0;
                        bool foundBlock = false;
                        foreach (var b in blockSeg.Blocks)
                        {
                            if (b.Id == blockToConvert.Id)
                            {
                                foundBlock = true;
                                continue;
                            }
                            bool isRemnantType = b.Type == BlockType.Remnant || b.Type == BlockType.CutRemnant;
                            if (!foundBlock)
                            {
                                if (isRemnantType) consecBefore++;
                                else consecBefore = 0;
                            }
                            else
                            {
                                if (isRemnantType) consecAfter++;
                                else break;
                            }
                        }
                        // Если конвертируем blockToConvert в CutRemnant, суммарный пробег = consecBefore + 1 + consecAfter
                        if (consecBefore + 1 + consecAfter > maxConsec)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[CONSOLIDATE] Row {row.RowIndex}: SKIP block W={blockToConvert.Width:F0} (C13: {consecBefore}+1+{consecAfter}>{maxConsec})");
                            continue; // оставляем [New]
                        }
                    }

                    // Создаём виртуальный остаток и конвертируем блок
                    var virtualRemnant = new Remnant
                    {
                        Id = Guid.NewGuid(),
                        Width = currentVirtualWidth,
                        Height = row.Height,
                        Source = "IntraRow_Virtual",
                        SourceBlockId = anchorBlock.Id,
                        CutHistory = $"Виртуальный (консолидация ряд {row.RowIndex})"
                    };
                    currentStock.Add(virtualRemnant);
                    allVirtualRemnants.Add(virtualRemnant);
                    binVirtualRemnants.Add(virtualRemnant);
                    intermediateVirtualIds.Add(virtualRemnant.Id); // все промежуточные — убираем последний ниже

                    blockToConvert.Type = BlockType.CutRemnant;
                    blockToConvert.RemnantId = virtualRemnant.Id;
                    blockToConvert.CutFrom = currentVirtualWidth;

                    currentVirtualWidth -= blockToConvert.Width;
                    totalConverted++;
                    anyConverted = true;
                }

                if (anyConverted)
                {
                    anchorIds.Add(anchorBlock.Id);
                    // Последний виртуальный остаток в цепочке — НЕ промежуточный
                    // (его "обрезок" UpdateStockAfterRow оставит в складе как реальный остаток)
                    if (binVirtualRemnants.Count > 0)
                        intermediateVirtualIds.Remove(binVirtualRemnants.Last().Id);

                    System.Diagnostics.Debug.WriteLine(
                        $"[CONSOLIDATE] Row {row.RowIndex}: bin anchor={anchorBlock.Width:F0}mm, converted={anyConverted}, final remnant={currentVirtualWidth:F0}mm");
                }
            }

            return (totalConverted, anchorIds, intermediateVirtualIds, allVirtualRemnants);
        }

        private static List<Remnant> UpdateStockAfterRow(
            RowSolution row,
            List<Remnant> currentStock,
            OptimizationConstraints constraints)
        {
            const double tol = 0.001;
            var usedIds = new HashSet<Guid>();
            var createdRemnants = new List<Remnant>();

            foreach (var seg in row.Segments)
            {
                foreach (var block in seg.Blocks)
                {
                    if (block.RemnantId.HasValue)
                        usedIds.Add(block.RemnantId.Value);

                    if (block.Type == BlockType.New)
                    {
                        double tileHeight = constraints.TileHeight;
                        double tileWidth = constraints.TileWidth;

                        // 1. Сначала отрезаем по высоте всего ряда (если ряд ниже плиты).
                        // Остаток по всей ширине плиты, высотой: TileHeight - RowHeight
                        if (row.Height < tileHeight - tol)
                        {
                            double leftoverHeight = tileHeight - row.Height;
                            if (leftoverHeight >= constraints.MinRemnantToSave)
                            {
                                var heightRemnant = new Remnant
                                {
                                    Width = tileWidth,
                                    Height = leftoverHeight,
                                    Source = "RollingHorizon_Height",
                                    CutHistory = $"Плита {tileHeight:F0}→{row.Height:F0}+{leftoverHeight:F0}",
                                    SourceBlockId = block.Id,
                                    CreatedAtRow = row.RowIndex
                                };
                                currentStock.Add(heightRemnant);
                                createdRemnants.Add(heightRemnant);
                                System.Diagnostics.Debug.WriteLine($"[ROW-FLOW] Created {heightRemnant.Width:F0}×{heightRemnant.Height:F0} (ShortId={heightRemnant.ShortId}) at row {row.RowIndex} src={heightRemnant.Source}");
                            }
                            else if (leftoverHeight > tol)
                            {
                                // BUG-6 fix: log discarded sub-threshold height remnant
                                System.Diagnostics.Debug.WriteLine(
                                    $"DISCARDED: {tileWidth:F0}×{leftoverHeight:F0}mm height remnant (below MinRemnantToSave={constraints.MinRemnantToSave:F0}mm) in row {row.RowIndex}");
                            }
                        }

                        // 2. Затем отрезаем по ширине (если используется не полная ширина плиты).
                        // Остаток имеет высоту ряда: RowHeight
                        if (block.Width < tileWidth - tol)
                        {
                            double leftoverWidth = tileWidth - block.Width;
                            if (leftoverWidth >= constraints.MinRemnantToSave)
                            {
                                var widthRemnant = new Remnant
                                {
                                    Width = leftoverWidth,
                                    Height = row.Height,
                                    Source = "RollingHorizon_Width",
                                    CutHistory = $"Плита {tileWidth:F0}→{block.Width:F0}+{leftoverWidth:F0}(ш)",
                                    SourceBlockId = block.Id,
                                    CreatedAtRow = row.RowIndex
                                };
                                currentStock.Add(widthRemnant);
                                createdRemnants.Add(widthRemnant);
                                System.Diagnostics.Debug.WriteLine($"[ROW-FLOW] Created {widthRemnant.Width:F0}×{widthRemnant.Height:F0} (ShortId={widthRemnant.ShortId}) at row {row.RowIndex} src={widthRemnant.Source}");
                            }
                            else if (leftoverWidth > tol)
                            {
                                // BUG-6 fix: log discarded sub-threshold width remnant
                                System.Diagnostics.Debug.WriteLine(
                                    $"DISCARDED: {leftoverWidth:F0}×{row.Height:F0}mm width remnant (below MinRemnantToSave={constraints.MinRemnantToSave:F0}mm) in row {row.RowIndex}");
                            }
                        }
                    }
                    else if (block.Type == BlockType.CutRemnant && block.RemnantId.HasValue)
                    {
                        var originalRemnant = currentStock.FirstOrDefault(r => r.Id == block.RemnantId.Value);
                        if (originalRemnant != null)
                        {
                            // Размеры исходного остатка после возможного поворота
                            double effWidth = originalRemnant.Width;
                            double effHeight = originalRemnant.Height;

                            if (block.IsRotated)
                            {
                                effWidth = originalRemnant.Height;
                                effHeight = originalRemnant.Width;
                            }

                            // 1. Остаток по высоте (от исходного остатка)
                            if (row.Height < effHeight - tol)
                            {
                                double leftoverHeight = effHeight - row.Height;
                                if (leftoverHeight >= constraints.MinRemnantToSave)
                                {
                                    string parentHistory = string.IsNullOrEmpty(originalRemnant.CutHistory)
                                        ? $"{effHeight:F0}" : originalRemnant.CutHistory;
                                    var heightRemnant = new Remnant
                                    {
                                        Width = effWidth,
                                        Height = leftoverHeight,
                                        Source = "RollingHorizon_HeightCut",
                                        ParentRemnantId = originalRemnant.Id,
                                        CutHistory = $"{parentHistory}→{row.Height:F0}+{leftoverHeight:F0}",
                                        SourceBlockId = block.Id,
                                        CreatedAtRow = row.RowIndex
                                    };
                                    currentStock.Add(heightRemnant);
                                    createdRemnants.Add(heightRemnant);
                                    System.Diagnostics.Debug.WriteLine($"[ROW-FLOW] Created {heightRemnant.Width:F0}×{heightRemnant.Height:F0} (ShortId={heightRemnant.ShortId}) at row {row.RowIndex} src={heightRemnant.Source}");
                                }
                                else if (leftoverHeight > tol)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"DISCARDED: {effWidth:F0}×{leftoverHeight:F0}mm heightCut remnant (below MinRemnantToSave={constraints.MinRemnantToSave:F0}mm) in row {row.RowIndex}");
                                }
                            }

                            // 2. Остаток по ширине блока
                            if (block.Width < effWidth - tol)
                            {
                                double leftoverWidth = effWidth - block.Width;
                                if (leftoverWidth >= constraints.MinRemnantToSave)
                                {
                                    string parentHistory = string.IsNullOrEmpty(originalRemnant.CutHistory)
                                        ? $"{effWidth:F0}" : originalRemnant.CutHistory;
                                    var widthRemnant = new Remnant
                                    {
                                        Width = leftoverWidth,
                                        Height = row.Height,
                                        Source = "RollingHorizon_WidthCut",
                                        ParentRemnantId = originalRemnant.Id,
                                        CutHistory = $"{parentHistory}→{block.Width:F0}+{leftoverWidth:F0}(ш)",
                                        SourceBlockId = block.Id,
                                        CreatedAtRow = row.RowIndex
                                    };
                                    currentStock.Add(widthRemnant);
                                    createdRemnants.Add(widthRemnant);
                                    System.Diagnostics.Debug.WriteLine($"[ROW-FLOW] Created {widthRemnant.Width:F0}×{widthRemnant.Height:F0} (ShortId={widthRemnant.ShortId}) at row {row.RowIndex} src={widthRemnant.Source}");
                                }
                                else if (leftoverWidth > tol)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"DISCARDED: {leftoverWidth:F0}×{row.Height:F0}mm widthCut remnant (below MinRemnantToSave={constraints.MinRemnantToSave:F0}mm) in row {row.RowIndex}");
                                }
                            }
                        }
                    }
                    else if (block.Type == BlockType.Remnant && block.RemnantId.HasValue && block.IsRotated)
                    {
                        // [NARROW-STRIP] Повёрнутый остаток, использованный полностью по ширине (BlockType.Remnant),
                        // но фактически обрезанный по высоте (эффективная высота > высоты ряда).
                        // Пример: 1200×200мм повёрнут → effWidth=200, effHeight=1200; ряд 600мм → остаток 200×600мм.
                        // Стандартный путь CutRemnant не задействован, поэтому высотный остаток создаётся здесь.
                        var originalRemnant = currentStock.FirstOrDefault(r => r.Id == block.RemnantId.Value);
                        if (originalRemnant != null)
                        {
                            double effHeight = originalRemnant.Width;  // после поворота: исходная Width стала Height
                            double effWidth  = originalRemnant.Height; // после поворота: исходная Height стала Width

                            if (row.Height < effHeight - tol)
                            {
                                double leftoverHeight = effHeight - row.Height;
                                if (leftoverHeight >= constraints.MinRemnantToSave)
                                {
                                    string parentHistory = string.IsNullOrEmpty(originalRemnant.CutHistory)
                                        ? $"{effHeight:F0}" : originalRemnant.CutHistory;
                                    // Храним Width=leftoverHeight, Height=effWidth чтобы GetUsableWidth
                                    // мог использовать остаток в ОБОИХ направлениях:
                                    //   • в ряду высотой effWidth (без поворота): Width=leftoverHeight
                                    //   • в ряду высотой leftoverHeight (повёрнуто): Height=effWidth
                                    // Пример: остаток 200×600 от полосы 1200×200 в ряду 600мм →
                                    //   сохраняется как 600×200мм: пригоден как 200мм-вставка
                                    //   в 600мм-ряду (повёрнуто) ИЛИ как 600мм-вставка в 200мм-ряду.
                                    var heightRemnant = new Remnant
                                    {
                                        Width = leftoverHeight,
                                        Height = effWidth,
                                        Source = "RollingHorizon_HeightCut",
                                        ParentRemnantId = originalRemnant.Id,
                                        CutHistory = $"{parentHistory}→{row.Height:F0}+{leftoverHeight:F0}",
                                        SourceBlockId = block.Id,
                                        CreatedAtRow = row.RowIndex
                                    };
                                    currentStock.Add(heightRemnant);
                                    createdRemnants.Add(heightRemnant);
                                    System.Diagnostics.Debug.WriteLine($"[ROW-FLOW] Created {heightRemnant.Width:F0}×{heightRemnant.Height:F0} (ShortId={heightRemnant.ShortId}) at row {row.RowIndex} src={heightRemnant.Source}");
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[NARROW-STRIP] Row {row.RowIndex}: rotated remnant " +
                                        $"{originalRemnant.Width:F0}×{originalRemnant.Height:F0} used as " +
                                        $"{effWidth:F0}×{row.Height:F0} → sub-remnant saved as " +
                                        $"{leftoverHeight:F0}×{effWidth:F0}mm (bidirectional)");
                                }
                                else if (leftoverHeight > tol)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[NARROW-STRIP] DISCARDED: {effWidth:F0}×{leftoverHeight:F0}mm " +
                                        $"height sub-remnant from rotated {originalRemnant.Width:F0}×{originalRemnant.Height:F0} " +
                                        $"(below MinRemnantToSave={constraints.MinRemnantToSave:F0}mm)");
                                }
                            }
                        }
                    }
                }
            }

#if DEBUG
            // [ROW-FLOW] Лог использования свежих остатков
            foreach (var r in currentStock.Where(r => usedIds.Contains(r.Id) && r.CreatedAtRow >= 0 && r.CreatedAtRow >= row.RowIndex - 3))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ROW-FLOW] Row {row.RowIndex} USED fresh remnant {r.ShortId} " +
                    $"({r.Width:F0}×{r.Height:F0}) created at row {r.CreatedAtRow}");
            }
#endif

            currentStock.RemoveAll(r => usedIds.Contains(r.Id));
            return createdRemnants;
        }

        private static OrToolsSolution BuildFinalSolution(
            FacadeInput fullInput,
            List<RowSolution> committedRows,
            List<Remnant> currentStock,
            List<Remnant> allCreatedRemnants,
            long totalSolveTimeMs,
            List<LBootElement> lBootElements = null)
        {
            var solution = new OrToolsSolution
            {
                FacadeId = fullInput.FacadeId,
                Rows = new List<RowSolution>(committedRows),
                StockAfter = new List<RemnantAfter>(),
                Status = SolverStatus.Optimal,
                SolveTimeMs = totalSolveTimeMs,
                LBootElements = lBootElements ?? new List<LBootElement>(),
                IntraFacadeRemnants = new List<Remnant>(allCreatedRemnants)
            };

            // Остатки, оставшиеся в текущем складе (без виртуальных IntraRow_Virtual)
            var remainingIds = new HashSet<Guid>(currentStock.Select(r => r.Id));

            foreach (var r in currentStock)
            {
                // Виртуальные IntraRow_Virtual к этому моменту должны быть удалены из currentStock,
                // но на всякий случай фильтруем их здесь.
                if (r.Source == "IntraRow_Virtual") continue;

                solution.StockAfter.Add(new RemnantAfter
                {
                    Id = r.Id,
                    Width = r.Width,
                    Height = r.Height,
                    Status = r.Source != null && r.Source.StartsWith("RollingHorizon")
                        ? RemnantStatus.CreatedFromNew
                        : r.Source == "LBoot_CutOut"
                            ? RemnantStatus.CreatedFromCut
                            : RemnantStatus.Unused,
                    SourceBlockId = r.SourceBlockId
                });
            }

            // Созданные и переиспользованные
            foreach (var r in allCreatedRemnants)
            {
                if (!remainingIds.Contains(r.Id))
                {
                    solution.StockAfter.Add(new RemnantAfter
                    {
                        Id = r.Id,
                        Width = r.Width,
                        Height = r.Height,
                        Status = RemnantStatus.CreatedAndReused,
                        SourceBlockId = r.SourceBlockId
                    });
                }
            }

            solution.TotalRemainingArea = solution.StockAfter
                .Where(x => x.Status != RemnantStatus.CreatedAndReused)
                .Sum(x => x.Width * x.Height) / 1_000_000.0;
            return solution;
        }
    }
}
