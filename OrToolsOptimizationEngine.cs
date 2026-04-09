using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;
using InsulationMasterPro.Data;

namespace InsulationMasterPro.OrTools
{
    /// <summary>
    /// Главный движок оптимизации на основе OR-Tools.
    /// Объединяет препроцессор, генератор паттернов, оптимизатор и постпроцессор.
    /// Заменяет жадный OptimizationEngine для глобальной оптимизации раскладки.
    /// </summary>
    public class OrToolsOptimizationEngine
    {
        private readonly OptimizerConfig _config;
        private readonly OptimizationConstraints _constraints;

        public OrToolsOptimizationEngine(OptimizerConfig? config = null, OptimizationConstraints? constraints = null)
        {
            _config = config ?? new OptimizerConfig();
            _constraints = constraints ?? new OptimizationConstraints();
        }

        /// <summary>
        /// Выполняет оптимизацию раскладки фасада.
        /// По умолчанию используется «скользящее окно» (2–3 ряда за раз) — проще и устойчивее.
        /// При UseRollingHorizon = false — полная глобальная оптимизация всего фасада.
        /// </summary>
        /// <param name="facade">Контур фасада (Polyline)</param>
        /// <param name="windows">Контуры окон</param>
        /// <param name="stockRemnants">Текущие остатки на складе</param>
        /// <param name="firstRowWithRelease">Начать выпуск с первого ряда</param>
        /// <param name="syncedRowHeights">Синхронизированные высоты рядов из первого фасада (для Row Sync)</param>
        /// <returns>Результат оптимизации (решение OR-Tools)</returns>
        public OrToolsSolution Optimize(
            Polyline facade,
            List<Polyline> windows,
            List<Remnant> stockRemnants,
            bool firstRowWithRelease = true,
            List<double>? syncedRowHeights = null,
            List<WindowInfo>? globalForecasterWindows = null)
        {
            if (_config.UseRollingHorizon)
            {
                var rolling = new RollingHorizonEngine(_config, _constraints)
                {
                    HorizonRows = _config.RollingHorizonRows,
                    TimeoutPerWindowSeconds = _config.RollingHorizonTimeoutPerWindowSeconds
                };
                return rolling.Optimize(facade, windows, stockRemnants, firstRowWithRelease, syncedRowHeights, globalForecasterWindows);
            }

            return OptimizeFullFacade(facade, windows, stockRemnants, firstRowWithRelease, syncedRowHeights, globalForecasterWindows);
        }

        /// <summary>
        /// Глобальная оптимизация всего фасада за один вызов (все ряды, все паттерны).
        /// </summary>
        private OrToolsSolution OptimizeFullFacade(
            Polyline facade,
            List<Polyline> windows,
            List<Remnant> stockRemnants,
            bool firstRowWithRelease,
            List<double>? syncedRowHeights = null,
            List<WindowInfo>? globalForecasterWindows = null)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // 1. Препроцессор: конвертируем геометрию AutoCAD в FacadeInput
                var input = Preprocessor.ConvertToFacadeInput(
                    facade,
                    windows,
                    stockRemnants,
                    firstRowWithRelease,
                    _constraints,
                    syncedRowHeights,
                    globalForecasterWindows);

                Log($"Препроцессор: {input.Rows.Count} рядов, {input.AllSegments.Count} сегментов, {input.StockRemnants.Count} остатков");

                // 2. Генератор паттернов: создаём все допустимые комбинации блоков
                var patterns = PatternGenerator.GeneratePatterns(input, _config);

                int totalPatterns = patterns.Values.Sum(p => p.Count);
                Log($"Генератор паттернов: {totalPatterns} паттернов для {patterns.Count} сегментов");

                // Собираем полную информацию для диагностики
                var inputSummary = BuildInputSummary(input, patterns);

                // Проверяем, что для каждого сегмента есть хотя бы один паттерн (иначе решение невозможно)
                var segmentsWithoutPatterns = patterns.Where(kv => kv.Value.Count == 0).ToList();
                if (segmentsWithoutPatterns.Any())
                {
                    var diag = new List<string>(inputSummary);
                    diag.Add("");
                    diag.Add("ПРИЧИНА ОШИБКИ: для следующих сегментов нет ни одного допустимого паттерна:");
                    foreach (var kv in segmentsWithoutPatterns)
                    {
                        var seg = input.Rows.FirstOrDefault(r => r.RowIndex == kv.Key.Item1)?
                            .Segments.FirstOrDefault(s => s.SegmentIndex == kv.Key.Item2);
                        double segW = seg?.Width ?? 0;
                        string msg = $"  — Ряд {kv.Key.Item1}, сегмент {kv.Key.Item2}: ширина={segW:F0} мм, паттернов=0";
                        diag.Add(msg);
                        Log($"  Нет паттернов для сегмента r{kv.Key.Item1}_s{kv.Key.Item2} (ширина={segW:F0} мм)");
                    }
                    diag.Add("");
                    diag.Add("Ограничения: MinBlock=" + _constraints.MinBlock + " мм, MinBlockNearWindow=" + _constraints.MinBlockNearWindow +
                        " мм, MinReleasePiece=" + _constraints.MinReleasePiece + " мм, TileWidth=" + _constraints.TileWidth + " мм");
                    return new OrToolsSolution
                    {
                        FacadeId = input.FacadeId,
                        Status = SolverStatus.Infeasible,
                        SolveTimeMs = stopwatch.ElapsedMilliseconds,
                        Diagnostics = diag
                    };
                }

                // Проверка перевязки: для каких пар рядов/сегментов нет ни одной допустимой пары паттернов
                var jointStaggerDiag = CollectJointStaggerDiagnostics(input, patterns);
                if (jointStaggerDiag.Count > 0)
                {
                    var diagMsg = new List<string> { "Обнаружены пары рядов/сегментов без допустимой перевязки (MinJointOffset " + _constraints.MinJointOffset + " мм):" };
                    diagMsg.AddRange(jointStaggerDiag);
                    Log(string.Join(Environment.NewLine, diagMsg));
                }

                // 3. Оптимизатор: решаем CP-SAT задачу (без фиксированного предыдущего ряда)
                var optimizer = new OrToolsOptimizer(input, patterns, _config);
                var solution = optimizer.Solve(fixedPreviousRowJoints: null);

                stopwatch.Stop();
                Log($"Оптимизатор: статус={solution.Status}, время={solution.SolveTimeMs}мс, " +
                    $"площадь остатков={solution.TotalRemainingArea:F2}м²");

                // Диагностика при неудаче решателя
                if (solution.Status != SolverStatus.Optimal && solution.Status != SolverStatus.Feasible)
                {
                    solution.Diagnostics ??= new List<string>();
                    solution.Diagnostics.AddRange(inputSummary);
                    solution.Diagnostics.Add("");
                    if (solution.Status == SolverStatus.Infeasible)
                    {
                        solution.Diagnostics.Add("ПРИЧИНА: Решатель CP-SAT вернул несовместимость (Infeasible).");
                        solution.Diagnostics.Add("Возможные причины: совокупность ограничений перевязки швов между рядами не допускает ни одной комбинации паттернов по всему фасаду.");
                        if (jointStaggerDiag.Count > 0)
                        {
                            solution.Diagnostics.Add("");
                            solution.Diagnostics.Add("Проблемные пары рядов (перевязка):");
                            foreach (var line in jointStaggerDiag)
                                solution.Diagnostics.Add(line);
                        }
                    }
                    else if (solution.Status == SolverStatus.Timeout)
                    {
                        solution.Diagnostics.Add($"ПРИЧИНА: Таймаут — решение не найдено за {_config.TimeoutSeconds} сек.");
                        solution.Diagnostics.Add("Попробуйте увеличить таймаут (OrToolsTimeoutSeconds) или упростить фасад.");
                    }
                    else
                    {
                        solution.Diagnostics.Add($"ПРИЧИНА: Ошибка решателя: {solution.Status}.");
                    }
                }

                return solution;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Log($"ОШИБКА: {ex.Message}\n{ex.StackTrace}");

                var diagLines = new List<string>
                {
                    $"Исключение при оптимизации: {ex.GetType().Name}: {ex.Message}",
                    $"Stack trace:",
                    ex.StackTrace ?? "(нет стека)"
                };
                if (ex.InnerException != null)
                {
                    diagLines.Add($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    diagLines.Add(ex.InnerException.StackTrace ?? "(нет стека)");
                }

                return new OrToolsSolution
                {
                    FacadeId = "error",
                    Status = SolverStatus.Error,
                    SolveTimeMs = stopwatch.ElapsedMilliseconds,
                    Diagnostics = diagLines
                };
            }
        }

        /// <summary>
        /// Собирает подробную информацию о входных данных для отчёта диагностики.
        /// </summary>
        private static List<string> BuildInputSummary(
            FacadeInput input,
            Dictionary<(int, int), List<BlockPattern>> patterns)
        {
            var lines = new List<string>();
            lines.Add("=== ВХОДНЫЕ ДАННЫЕ ===");
            lines.Add($"Фасад: {input.Width:F0} × {input.Height:F0} мм (MinX={input.MinX:F0}, MinY={input.MinY:F0})");
            lines.Add($"Окон: {input.Windows.Count}");
            lines.Add($"Остатков на складе: {input.StockRemnants.Count} шт. ({input.StockRemnants.Sum(r => r.Area) / 1_000_000.0:F3} м²)");
            lines.Add($"Рядов: {input.Rows.Count}");
            lines.Add("");
            lines.Add("=== РЯДЫ И СЕГМЕНТЫ ===");
            foreach (var row in input.Rows)
            {
                lines.Add($"Ряд {row.RowIndex}: Y={row.Y:F0}, высота={row.Height:F0} мм, выпуск={row.HasRelease}, сегментов={row.Segments.Count}");
                foreach (var seg in row.Segments)
                {
                    var key = (row.RowIndex, seg.SegmentIndex);
                    int patCount = patterns.TryGetValue(key, out var pats) ? pats.Count : 0;
                    lines.Add($"    Сегмент {seg.SegmentIndex}: X=[{seg.StartX:F0}..{seg.EndX:F0}], ширина={seg.Width:F0} мм, " +
                        $"окноЛево={seg.NearWindowLeft}, окноПраво={seg.NearWindowRight}, " +
                        $"выпускЛево={seg.IsReleaseLeft}, выпускПраво={seg.IsReleaseRight}, " +
                        $"паттернов={patCount}");
                }
            }
            lines.Add("");
            lines.Add("=== ОГРАНИЧЕНИЯ ===");
            lines.Add($"MinBlock={input.Constraints.MinBlock} мм, MinBlockNearWindow={input.Constraints.MinBlockNearWindow} мм");
            lines.Add($"MinJointOffset={input.Constraints.MinJointOffset} мм, MaxConsecutiveRemnants={input.Constraints.MaxConsecutiveRemnants}");
            lines.Add($"ReleaseOverhang={input.Constraints.ReleaseOverhang} мм, MinReleasePiece={input.Constraints.MinReleasePiece} мм");
            lines.Add($"TileWidth={input.Constraints.TileWidth} мм, TileHeight={input.Constraints.TileHeight} мм");
            lines.Add($"MinRemnantToSave={input.Constraints.MinRemnantToSave} мм");
            if (input.StockRemnants.Count > 0)
            {
                lines.Add("");
                lines.Add("=== ОСТАТКИ НА СКЛАДЕ ===");
                foreach (var r in input.StockRemnants.Take(30))
                {
                    lines.Add($"  {r.Width:F0} × {r.Height:F0} мм (площадь={r.Area / 1_000_000.0:F4} м²) id={r.Id}");
                }
                if (input.StockRemnants.Count > 30)
                    lines.Add($"  ... и ещё {input.StockRemnants.Count - 30} шт.");
            }
            return lines;
        }

        /// <summary>
        /// Собирает диагностику по перевязке: пары (ряд N, ряд N-1) и сегменты, для которых нет ни одной допустимой пары паттернов.
        /// </summary>
        private static List<string> CollectJointStaggerDiagnostics(
            FacadeInput input,
            Dictionary<(int, int), List<BlockPattern>> patterns)
        {
            const double TOL = 0.001;
            var diag = new List<string>();
            double minOffset = input.Constraints.MinJointOffset;

            for (int r = 1; r < input.Rows.Count; r++)
            {
                var currentRow = input.Rows[r];
                var prevRow = input.Rows[r - 1];

                foreach (var currentSegment in currentRow.Segments)
                {
                    foreach (var prevSegment in prevRow.Segments)
                    {
                        if (currentSegment.EndX <= prevSegment.StartX || currentSegment.StartX >= prevSegment.EndX)
                            continue;

                        var currentKey = (currentRow.RowIndex, currentSegment.SegmentIndex);
                        var prevKey = (prevRow.RowIndex, prevSegment.SegmentIndex);
                        if (!patterns.TryGetValue(currentKey, out var currentPatterns) || currentPatterns.Count == 0)
                            continue;
                        if (!patterns.TryGetValue(prevKey, out var prevPatterns) || prevPatterns.Count == 0)
                            continue;

                        int allowed = 0;
                        for (int i = 0; i < currentPatterns.Count; i++)
                        {
                            for (int j = 0; j < prevPatterns.Count; j++)
                            {
                                if (IsJointStaggerValid(currentPatterns[i], prevPatterns[j], minOffset, TOL))
                                    allowed++;
                            }
                        }
                        if (allowed == 0)
                        {
                            diag.Add($"  — Ряд {r} (сегмент {currentSegment.SegmentIndex}) и ряд {r - 1} (сегмент {prevSegment.SegmentIndex}): нет ни одной допустимой пары паттернов по перевязке (требуется ≥ {minOffset} мм).");
                        }
                    }
                }
            }
            return diag;
        }

        private static bool IsJointStaggerValid(BlockPattern a, BlockPattern b, double minOffset, double tol)
        {
            foreach (var j1 in a.JointPositions)
            {
                foreach (var j2 in b.JointPositions)
                {
                    if (Math.Abs(j1 - j2) < minOffset - tol)
                        return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Выполняет оптимизацию и возвращает LayoutResult для интеграции с существующим кодом
        /// </summary>
        public LayoutResult OptimizeAndConvert(
            Polyline facade,
            List<Polyline> windows,
            List<Remnant> stockRemnants,
            bool firstRowWithRelease = true)
        {
            var input = Preprocessor.ConvertToFacadeInput(
                facade, windows, stockRemnants, firstRowWithRelease, _constraints);

            var solution = Optimize(facade, windows, stockRemnants, firstRowWithRelease);

            return Postprocessor.ConvertToLayoutResult(solution, input, stockRemnants);
        }

        /// <summary>
        /// Генерирует текстовый отчёт по результатам оптимизации
        /// </summary>
        public string GenerateReport(
            OrToolsSolution solution,
            LayoutResult result,
            Polyline facade,
            List<Polyline> windows,
            List<Remnant> stockRemnants,
            bool firstRowWithRelease)
        {
            var input = Preprocessor.ConvertToFacadeInput(
                facade, windows, stockRemnants, firstRowWithRelease, _constraints);

            return Postprocessor.GenerateReport(solution, result, input);
        }

        /// <summary>
        /// Проверяет, возможна ли оптимизация для данного фасада
        /// </summary>
        public bool CanOptimize(Polyline facade, List<Polyline> windows)
        {
            try
            {
                var ext = facade.GeometricExtents;
                var width = ext.MaxPoint.X - ext.MinPoint.X;
                var height = ext.MaxPoint.Y - ext.MinPoint.Y;

                // Проверяем минимальные размеры
                if (width < _constraints.MinBlock || height < _constraints.MinRowHeight)
                    return false;

                // Проверяем максимальные размеры (ограничение производительности)
                if (width > 15000 || height > 6000)
                {
                    Log($"ВНИМАНИЕ: Большой фасад ({width:F0}×{height:F0}мм), оптимизация может занять больше времени");
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Оценивает сложность задачи (количество паттернов, время решения)
        /// </summary>
        public (int estimatedPatterns, int estimatedTimeSeconds) EstimateComplexity(
            Polyline facade,
            List<Polyline> windows,
            List<Remnant> stockRemnants)
        {
            var input = Preprocessor.ConvertToFacadeInput(
                facade, windows, stockRemnants, true, _constraints);

            // Приблизительная оценка
            int segmentsCount = input.AllSegments.Count;
            int remnantsCount = stockRemnants.Count;

            // Примерное количество паттернов: ~100-500 на сегмент
            int patternsPerSegment = Math.Min(500, 50 + remnantsCount * 10);
            int totalPatterns = segmentsCount * patternsPerSegment;

            // Примерное время: 1-2 секунды на 1000 паттернов
            int estimatedSeconds = Math.Max(1, totalPatterns / 500);

            return (totalPatterns, Math.Min(estimatedSeconds, _config.TimeoutSeconds));
        }

        private void Log(string message)
        {
            if (_config.LogProgress)
            {
                System.Diagnostics.Debug.WriteLine($"[OrTools] {message}");
                // Также можно выводить в AutoCAD Editor, если доступен
            }
        }
    }
}
