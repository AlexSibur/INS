using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Google.OrTools.Sat;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.OrTools
{
    /// <summary>
    /// Оптимизатор на основе Google OR-Tools CP-SAT.
    /// Минимизирует суммарную площадь остатков на складе после раскладки.
    /// </summary>
    public class OrToolsOptimizer
    {
        private const double TOLERANCE = 0.001;
        private const int SCALE = 100; // Масштаб для преобразования double → int (OR-Tools работает с целыми)

        // Множитель для per-remnant бинарного бонуса (IsUsed × effectiveArea × STOCK_BONUS_MULTIPLIER).
        // Без множителя бонус = ~50-72, незначителен vs TileWeight=10000.
        // С 50: max бонус = 72×50 = 3600 (значимый вторичный сигнал, но ниже TileWeight).
        private const int STOCK_BONUS_MULTIPLIER = 50;

        // Штраф за каждый неиспользованный остаток склада (не VirtualCutout).
        // Величина ниже STOCK_BONUS_MULTIPLIER, чтобы не конкурировать с бонусом за использование.
        // Создаёт симметричное давление: бонус за использование + штраф за неиспользование.
        private const int UNUSED_STOCK_PENALTY_FACTOR = 20;

        private readonly FacadeInput _input;
        private readonly Dictionary<(int, int), List<BlockPattern>> _patterns;
        private readonly OptimizerConfig _config;

        /// <summary>При использовании Rolling Horizon — отфильтрованные паттерны для первого ряда (перевязка с фиксированным предыдущим).</summary>
        private Dictionary<(int, int), List<BlockPattern>> _effectivePatterns;

        public OrToolsOptimizer(
            FacadeInput input,
            Dictionary<(int, int), List<BlockPattern>> patterns,
            OptimizerConfig config)
        {
            _input = input;
            _patterns = patterns;
            _config = config;
            _effectivePatterns = null;
        }

        /// <summary>
        /// Запускает оптимизацию и возвращает решение.
        /// При использовании в режиме «скользящего окна» передайте стыки уже уложенного предыдущего ряда —
        /// для первого ряда окна будут допустимы только паттерны с перевязкой >= MinJointOffset с этими стыками.
        /// </summary>
        /// <param name="fixedPreviousRowJoints">Список X-координат стыков предыдущего ряда (уже зафиксированного). null = не используется.</param>
        public OrToolsSolution Solve(IReadOnlyList<double>? fixedPreviousRowJoints = null)
        {
            var stopwatch = Stopwatch.StartNew();

            // Для Rolling Horizon: первый ряд окна должен соблюдать перевязку с уже уложенным предыдущим рядом
            _effectivePatterns = BuildEffectivePatterns(fixedPreviousRowJoints);

            var model = new CpModel();

            // 1. Переменные выбора паттерна для каждого сегмента
            var patternVars = CreatePatternVariables(model);

            // 2. Переменные использования остатков
            var remnantUsedVars = CreateRemnantUsedVariables(model);

            // 3. Ограничение: каждый остаток используется не более 1 раза
            AddRemnantUsageConstraints(model, patternVars, remnantUsedVars);

            // 4. Ограничение: перевязка швов >= 100 мм
            AddJointStaggerConstraints(model, patternVars);

            // 5. Целевая функция: минимизировать площадь остатков на складе
            var objective = BuildObjective(model, patternVars, remnantUsedVars);
            model.Minimize(objective);

            // 6. Решение
            var solver = new CpSolver();
            solver.StringParameters = $"max_time_in_seconds:{_config.TimeoutSeconds}";
            if (_config.LogProgress)
            {
                solver.StringParameters += ",log_search_progress:true";
            }

            var status = solver.Solve(model);
            stopwatch.Stop();

            var solution = ExtractSolution(solver, status, patternVars, remnantUsedVars, stopwatch.ElapsedMilliseconds);

            // solver.Value() and solver.ObjectiveValue are only valid when a solution was found
            if (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible)
            {
                int newTilesCount = solution.Rows
                    .SelectMany(r => r.Segments)
                    .SelectMany(s => s.Blocks)
                    .Count(b => b.Type == BlockType.New);
                int remnantsUsedCount = _input.StockRemnants
                    .Count(rem => remnantUsedVars.ContainsKey(rem.Id) &&
                                  solver.Value(remnantUsedVars[rem.Id]) == 1);
                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [OrToolsOptimizer] solve result: " +
                    $"status={status} newTileBlocks={newTilesCount} remnantsUsed={remnantsUsedCount} " +
                    $"objective={solver.ObjectiveValue:F0} solveTime={stopwatch.ElapsedMilliseconds}ms");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [OrToolsOptimizer] solve result: " +
                    $"status={status} NO_SOLUTION solveTime={stopwatch.ElapsedMilliseconds}ms");
            }

            _effectivePatterns = null;
            return solution;
        }

        /// <summary>
        /// Строит словарь паттернов: для первого ряда окна — только паттерны с перевязкой с fixedPreviousRowJoints.
        /// </summary>
        private Dictionary<(int, int), List<BlockPattern>> BuildEffectivePatterns(IReadOnlyList<double> fixedPreviousRowJoints)
        {
            if (fixedPreviousRowJoints == null || fixedPreviousRowJoints.Count == 0 || _input.Rows.Count == 0)
                return null;

            int firstRowIndex = _input.Rows[0].RowIndex;
            double minOffset = _input.Constraints.MinJointOffset;
            var result = new Dictionary<(int, int), List<BlockPattern>>();

            foreach (var kv in _patterns)
            {
                if (kv.Key.Item1 == firstRowIndex)
                {
                    var filtered = kv.Value.Where(p => IsJointStaggerValidWithFixed(p, fixedPreviousRowJoints, minOffset)).ToList();
                    result[kv.Key] = filtered.Count > 0 ? filtered : kv.Value;
                }
                else
                {
                    result[kv.Key] = kv.Value;
                }
            }

            return result;
        }

        private bool IsJointStaggerValidWithFixed(BlockPattern pattern, IReadOnlyList<double> fixedJoints, double minOffset)
        {
            foreach (var j1 in pattern.JointPositions)
            {
                foreach (var j2 in fixedJoints)
                {
                    if (Math.Abs(j1 - j2) < minOffset - TOLERANCE)
                        return false;
                }
            }
            return true;
        }

        private Dictionary<(int, int), List<BlockPattern>> GetPatterns()
        {
            return _effectivePatterns ?? _patterns;
        }

        /// <summary>
        /// Создаёт переменные выбора паттерна для каждого сегмента
        /// </summary>
        private Dictionary<(int, int), IntVar> CreatePatternVariables(CpModel model)
        {
            var vars = new Dictionary<(int, int), IntVar>();

            foreach (var row in _input.Rows)
            {
                foreach (var segment in row.Segments)
                {
                    var key = (row.RowIndex, segment.SegmentIndex);
                    var patternsDict = GetPatterns();
                    if (patternsDict.TryGetValue(key, out var patterns) && patterns.Count > 0)
                    {
                        vars[key] = model.NewIntVar(0, patterns.Count - 1, $"pat_r{row.RowIndex}_s{segment.SegmentIndex}");
                    }
                }
            }

            return vars;
        }

        /// <summary>
        /// Создаёт переменные использования остатков
        /// </summary>
        private Dictionary<Guid, BoolVar> CreateRemnantUsedVariables(CpModel model)
        {
            var vars = new Dictionary<Guid, BoolVar>();

            foreach (var remnant in _input.StockRemnants)
            {
                vars[remnant.Id] = model.NewBoolVar($"ru_{remnant.Id}");
            }

            return vars;
        }

        /// <summary>
        /// Добавляет ограничение: каждый остаток используется не более 1 раза
        /// </summary>
        private void AddRemnantUsageConstraints(
            CpModel model,
            Dictionary<(int, int), IntVar> patternVars,
            Dictionary<Guid, BoolVar> remnantUsedVars)
        {
            foreach (var remnant in _input.StockRemnants)
            {
                var remnantUsed = remnantUsedVars[remnant.Id];

                // Собираем все паттерны, которые используют этот остаток
                var usageTerms = new List<(IntVar patternVar, int patternIndex)>();

                foreach (var kvp in GetPatterns())
                {
                    var (rowIndex, segIndex) = kvp.Key;
                    var patterns = kvp.Value;

                    for (int i = 0; i < patterns.Count; i++)
                    {
                        if (patterns[i].UsedRemnantIds.Contains(remnant.Id))
                        {
                            usageTerms.Add((patternVars[(rowIndex, segIndex)], i));
                        }
                    }
                }

                if (usageTerms.Count == 0)
                {
                    // Остаток не используется ни в одном паттерне
                    model.Add(remnantUsed == 0);
                    continue;
                }

                // remnantUsed = 1 тогда и только тогда, когда хотя бы один паттерн использует этот остаток
                // Используем channeling constraints
                var usageIndicators = new List<BoolVar>();
                foreach (var (patternVar, patternIndex) in usageTerms)
                {
                    var indicator = model.NewBoolVar($"uses_{remnant.Id}_{patternVar.Name()}_{patternIndex}");
                    // indicator = 1 iff patternVar == patternIndex
                    model.Add(patternVar == patternIndex).OnlyEnforceIf(indicator);
                    model.Add(patternVar != patternIndex).OnlyEnforceIf(indicator.Not());
                    usageIndicators.Add(indicator);
                }

                // Сумма индикаторов <= 1 (остаток используется не более 1 раза)
                model.Add(LinearExpr.Sum(usageIndicators) <= 1);

                // remnantUsed = sum(usageIndicators)
                model.Add(remnantUsed == LinearExpr.Sum(usageIndicators));
            }
        }

        /// <summary>
        /// Добавляет ограничение перевязки швов между соседними рядами
        /// </summary>
        private void AddJointStaggerConstraints(
            CpModel model,
            Dictionary<(int, int), IntVar> patternVars)
        {
            int minJointOffset = (int)(_input.Constraints.MinJointOffset * SCALE);

            for (int r = 1; r < _input.Rows.Count; r++)
            {
                var currentRow = _input.Rows[r];
                var prevRow = _input.Rows[r - 1];

                // Для каждой пары сегментов проверяем перевязку
                foreach (var currentSegment in currentRow.Segments)
                {
                    foreach (var prevSegment in prevRow.Segments)
                    {
                        // Сегменты должны перекрываться по X
                        if (currentSegment.EndX <= prevSegment.StartX || currentSegment.StartX >= prevSegment.EndX)
                            continue;

                        var currentKey = (currentRow.RowIndex, currentSegment.SegmentIndex);
                        var prevKey = (prevRow.RowIndex, prevSegment.SegmentIndex);

                        if (!patternVars.ContainsKey(currentKey) || !patternVars.ContainsKey(prevKey))
                            continue;

                        var patternsDict = GetPatterns();
                        var currentPatterns = patternsDict[currentKey];
                        var prevPatterns = patternsDict[prevKey];

                        // Формируем список допустимых комбинаций паттернов
                        var allowedPairs = new List<long[]>();

                        for (int i = 0; i < currentPatterns.Count; i++)
                        {
                            for (int j = 0; j < prevPatterns.Count; j++)
                            {
                                if (IsJointStaggerValid(currentPatterns[i], prevPatterns[j], _input.Constraints.MinJointOffset))
                                {
                                    allowedPairs.Add(new long[] { i, j });
                                }
                            }
                        }

                        if (allowedPairs.Count == 0)
                        {
                            // Нет допустимых комбинаций для перевязки — пропускаем это ограничение
                            // (геометрия не позволяет соблюсти MinJointOffset, но solver продолжит работу)
                            continue;
                        }

                        if (allowedPairs.Count < currentPatterns.Count * prevPatterns.Count)
                        {
                            // Добавляем Table constraint только если есть запрещённые комбинации
                            var tableConstraint = model.AddAllowedAssignments(new[] { patternVars[currentKey], patternVars[prevKey] });
                            foreach (var pair in allowedPairs)
                            {
                                tableConstraint.AddTuple(pair);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Проверяет, соблюдается ли перевязка швов между двумя паттернами
        /// </summary>
        private bool IsJointStaggerValid(BlockPattern pattern1, BlockPattern pattern2, double minOffset)
        {
            foreach (var joint1 in pattern1.JointPositions)
            {
                foreach (var joint2 in pattern2.JointPositions)
                {
                    if (Math.Abs(joint1 - joint2) < minOffset - TOLERANCE)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private LinearExpr BuildObjective(
            CpModel model,
            Dictionary<(int, int), IntVar> patternVars,
            Dictionary<Guid, BoolVar> remnantUsedVars)
        {
            var terms = new List<LinearExpr>();

            // Многокомпонентная целевая функция:
            //
            // Режим СТАНДАРТНЫЙ (EnableRowLevelObjective=false):
            //   min  Σ(new_tile_count_сегмента * TILE_WEIGHT)   ← PRIMARY
            //
            // Режим ROW-LEVEL (EnableRowLevelObjective=true):
            //   min  Σ_рядов (ceil(Σ NewWidth_сегментов_ряда / TileWidth) * TILE_WEIGHT)  ← PRIMARY
            //        Правильно моделирует физические плиты: 4 куска по 200мм = 1 плита.
            //
            // Общие члены (оба режима):
            //    - Σ(used_remnant_area * REMNANT_WEIGHT)    ← поощрение утилизации остатков
            //    + Σ(created_remnant_area)                  ← штраф за создание остатков
            //    + Σ(cut_count)                             ← вторичный: меньше резов
            //    + Σ(unused_stock_area)                     ← штраф за неиспользованные остатки
            int TILE_WEIGHT = _config.TileWeight;
            int REMNANT_USAGE_WEIGHT = _config.RemnantUsageWeight;
            double tileWidth = _input.Constraints.TileWidth;
            int tileWidthInt = (int)Math.Round(tileWidth);

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [OrToolsOptimizer.BuildObjective] " +
                $"TileWeight={TILE_WEIGHT} RemnantUsageWeight={REMNANT_USAGE_WEIGHT} " +
                $"StockBonusMultiplier={STOCK_BONUS_MULTIPLIER}");

            foreach (var kvp in GetPatterns())
            {
                var key = kvp.Key;
                var patterns = kvp.Value;

                if (!patternVars.ContainsKey(key))
                    continue;

                var patternVar = patternVars[key];

                // Бонус за используемую площадь остатков
                var usedAreas = patterns.Select(p => (long)(p.UsedRemnantArea * SCALE)).ToArray();
                if (usedAreas.Max() > 0)
                {
                    var usedAreaVar = model.NewIntVar(0, usedAreas.Max(), $"used_{key.Item1}_{key.Item2}");
                    model.AddElement(patternVar, usedAreas, usedAreaVar);
                    terms.Add(-usedAreaVar * REMNANT_USAGE_WEIGHT);
                }

                // PRIMARY (стандартный режим): per-segment количество новых плит
                if (!_config.EnableRowLevelObjective)
                {
                    var newTileCounts = patterns.Select(p => (long)p.NewTileCount).ToArray();
                    if (newTileCounts.Max() > 0)
                    {
                        var tileCountVar = model.NewIntVar(0, newTileCounts.Max(), $"tiles_{key.Item1}_{key.Item2}");
                        model.AddElement(patternVar, newTileCounts, tileCountVar);
                        terms.Add(TILE_WEIGHT * tileCountVar);
                    }
                }

                // Штраф за площадь создаваемых остатков
                var createdAreas = patterns.Select(p => (long)(p.CreatedRemnantArea * SCALE)).ToArray();
                if (createdAreas.Any(a => a != 0))
                {
                    var createdAreaVar = model.NewIntVar(0, createdAreas.Max(), $"created_{key.Item1}_{key.Item2}");
                    model.AddElement(patternVar, createdAreas, createdAreaVar);
                    terms.Add(createdAreaVar);
                }

                // Вторичный: предпочитать паттерны с меньшим числом резанных новых плит
                var cutNewCounts = patterns.Select(p => (long)p.Blocks.Count(b =>
                    b.Type == BlockType.New && b.Width < tileWidth - TOLERANCE)).ToArray();
                if (cutNewCounts.Max() > 0)
                {
                    var cutVar = model.NewIntVar(0, cutNewCounts.Max(), $"cut_{key.Item1}_{key.Item2}");
                    model.AddElement(patternVar, cutNewCounts, cutVar);
                    terms.Add(cutVar);
                }
            }

            // PRIMARY (row-level режим): ceil(Σ NewWidth_ряда / TileWidth) * TILE_WEIGHT
            // Корректно моделирует физические плиты: 4 куска 200+200+200+240 = 1 плита.
            if (_config.EnableRowLevelObjective)
            {
                var patternsDict = GetPatterns();

                foreach (var row in _input.Rows)
                {
                    var segNewWidthVars = new List<IntVar>();
                    long maxRowNewWidth = 0;

                    foreach (var segment in row.Segments)
                    {
                        var key = (row.RowIndex, segment.SegmentIndex);
                        if (!patternVars.ContainsKey(key) || !patternsDict.ContainsKey(key))
                            continue;

                        var patterns = patternsDict[key];
                        var patternVar = patternVars[key];

                        // Суммарная ширина [New] блоков в каждом паттерне сегмента
                        var newWidths = patterns.Select(p =>
                            (long)Math.Round(p.Blocks
                                .Where(b => b.Type == BlockType.New)
                                .Sum(b => b.Width)))
                            .ToArray();

                        long maxSegNewWidth = newWidths.Max();
                        if (maxSegNewWidth <= 0) continue; // сегмент без новых плит

                        var newWidthVar = model.NewIntVar(0, maxSegNewWidth,
                            $"nw_{row.RowIndex}_{segment.SegmentIndex}");
                        model.AddElement(patternVar, newWidths, newWidthVar);
                        segNewWidthVars.Add(newWidthVar);
                        maxRowNewWidth += maxSegNewWidth;
                    }

                    if (segNewWidthVars.Count == 0) continue;

                    // rowNewWidth = сумма ширин новых блоков по всем сегментам ряда
                    var rowNewWidthVar = model.NewIntVar(0, maxRowNewWidth, $"row_nw_{row.RowIndex}");
                    model.Add(rowNewWidthVar == LinearExpr.Sum(segNewWidthVars));

                    // actualTiles = ceil(rowNewWidth / tileWidth)
                    int maxTiles = (int)Math.Ceiling((double)maxRowNewWidth / tileWidthInt) + 1;
                    var actualTilesVar = model.NewIntVar(0, maxTiles, $"act_tiles_r{row.RowIndex}");

                    // Ограничение 1: actualTiles * TW >= rowNewWidth
                    // → actualTiles * TW - rowNewWidth >= 0
                    model.Add(LinearExpr.WeightedSum(
                        new[] { actualTilesVar, rowNewWidthVar },
                        new long[] { tileWidthInt, -1L }) >= 0L);

                    // Ограничение 2: rowNewWidth > (actualTiles-1) * TW
                    // → rowNewWidth - actualTiles * TW >= -(TW-1)
                    model.Add(LinearExpr.WeightedSum(
                        new[] { rowNewWidthVar, actualTilesVar },
                        new long[] { 1L, (long)(-tileWidthInt) }) >= (long)(-(tileWidthInt - 1)));

                    terms.Add(TILE_WEIGHT * actualTilesVar);
                }
            }

            // Бонус за использование каждого склочного остатка (бинарная переменная).
            // B1 FIX: Вместо фиксированного cap (maxSafeArea=199 для всех), используем
            // RemnantUsageWeight-зависимое масштабирование бонуса. Это позволяет pass2
            // (с бóльшим RemnantUsageWeight) реально давать бóльший бонус за остатки,
            // разблокируя дифференциацию multi-pass.
            //
            // Safety invariant сохранён: bonus = normalizedArea × RemnantUsageWeight × STOCK_BONUS_MULTIPLIER < TILE_WEIGHT.
            // normalizedArea = remnant.Area / maxPossibleArea ∈ [0, 1], масштабирован в long.
            // maxPossibleArea = TileWidth × TileHeight (максимальный остаток = целая плита).
            var currentRowIndices = new HashSet<int>(_input.Rows.Select(r => r.RowIndex));
            double hintPenaltyFactor = _config.RemnantPreAllocationHintPenalty > 0
                ? _config.RemnantPreAllocationHintPenalty : 0.5;

            // B1: Вычисляем максимальную площадь одного остатка для нормализации
            double maxPossibleArea = _input.Constraints.TileWidth * _input.Constraints.TileHeight;
            if (maxPossibleArea <= 0) maxPossibleArea = 1200.0 * 600.0; // fallback

            // B1: Масштабный коэффициент для безопасного бонуса.
            // bonus = scaledBonus × STOCK_BONUS_MULTIPLIER < TILE_WEIGHT
            // scaledBonus = (area / maxArea) × NORMALIZATION_SCALE × impCMultiplier
            // NORMALIZATION_SCALE выбран так, чтобы при RemnantUsageWeight=135 и STOCK_BONUS_MULTIPLIER=50:
            // max bonus ≈ 135 × 50 = 6750 < TILE_WEIGHT=10000 ✓
            const long NORMALIZATION_SCALE = 100L;

            foreach (var remnant in _input.StockRemnants)
            {
                if (!remnantUsedVars.ContainsKey(remnant.Id)) continue;
                long areaScaled = (long)(remnant.Area * SCALE);
                if (areaScaled <= 0) continue;

                // B1: Нормализуем площадь остатка относительно максимальной плиты [0..NORMALIZATION_SCALE]
                double areaRatio = Math.Min(remnant.Area / maxPossibleArea, 1.0);
                long normalizedArea = (long)(areaRatio * NORMALIZATION_SCALE);
                if (normalizedArea <= 0) normalizedArea = 1;

                // Improvement C: приоритетный бонус для остатков с min стороной ≥ ImprovementCMinDim мм.
                // EnableNarrowStripBonus: дополнительный путь для узких полос (max сторона ≥ TileHeight).
                double minDim = Math.Min(remnant.Width, remnant.Height);
                double maxDim = Math.Max(remnant.Width, remnant.Height);
                double tileHeight = _input.Constraints.TileHeight;
                bool impCApplied = false;
                bool isNarrowStrip = maxDim >= tileHeight && minDim < _config.ImprovementCMinDim;

                // B1: ImpC-бонус теперь аддитивный +50% вместо ×5, чтобы не пробить safety cap
                long impCBonus = 0;
                if (remnant.Source != "VirtualCutout" &&
                    (minDim >= _config.ImprovementCMinDim ||
                     (_config.EnableNarrowStripBonus && isNarrowStrip)))
                {
                    impCBonus = normalizedArea / 2; // +50% к базовому бонусу
                    impCApplied = true;
                }

                long effectiveArea = normalizedArea + impCBonus;

                // B1: Safety cap на основе RemnantUsageWeight:
                // effectiveArea × STOCK_BONUS_MULTIPLIER должен быть < TILE_WEIGHT
                // Но RemnantUsageWeight уже применён через continuous area bonus (lines 384-391),
                // а per-remnant binary bonus — дополнительный сигнал.
                // Итоговый cap: effectiveArea × STOCK_BONUS_MULTIPLIER < TILE_WEIGHT
                long maxSafeArea = Math.Max(1L, (TILE_WEIGHT - 1L) / STOCK_BONUS_MULTIPLIER);
                effectiveArea = Math.Min(effectiveArea, maxSafeArea);

                // Pre-allocation soft hint: снижаем бонус для остатков, предназначенных другому ряду.
                bool hintPenaltyApplied = false;
                if (_input.RemnantRowHints.TryGetValue(remnant.Id, out int preferredRow)
                    && !currentRowIndices.Contains(preferredRow))
                {
                    effectiveArea = (long)(effectiveArea * hintPenaltyFactor);
                    hintPenaltyApplied = true;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [OrToolsOptimizer] Remnant {remnant.Id} " +
                    $"({remnant.Width:F0}x{remnant.Height:F0}): " +
                    $"area={remnant.Area:F0}mm² normalizedArea={normalizedArea} effectiveArea={effectiveArea} " +
                    $"bonus={effectiveArea * STOCK_BONUS_MULTIPLIER} improvC={impCApplied} " +
                    $"narrowStrip={isNarrowStrip} hintPenalty={hintPenaltyApplied} minDim={minDim:F0}mm");

                terms.Add(remnantUsedVars[remnant.Id] * (-(effectiveArea * STOCK_BONUS_MULTIPLIER)));
            }

            // Явный штраф за неиспользованные остатки склада (реальные, не VirtualCutout).
            // Реализован через (1 - remnantUsedVar) × unusedPenalty:
            //   если остаток использован → (1-1)×penalty = 0
            //   если остаток не использован → (1-0)×penalty = penalty
            // Это создаёт симметричное давление: бонус за использование + штраф за неиспользование.
            long totalUnusedPenalty = 0;
            int unusedPenaltyRemnants = 0;
            foreach (var remnant in _input.StockRemnants)
            {
                if (remnant.Source == "VirtualCutout") continue;
                if (!remnantUsedVars.ContainsKey(remnant.Id)) continue;
                long areaScaledP = (long)(remnant.Area * SCALE);
                if (areaScaledP <= 0) continue;

                long unusedPenalty = areaScaledP * UNUSED_STOCK_PENALTY_FACTOR;
                // (1 - remnantUsedVar) × penalty = penalty - remnantUsedVar × penalty
                // Константа penalty добавляет фиксированный сдвиг (не влияет на оптимизацию),
                // а вычтенный remnantUsedVar × penalty поощряет использование.
                terms.Add(LinearExpr.WeightedSum(
                    new[] { remnantUsedVars[remnant.Id] },
                    new long[] { -unusedPenalty }));
                totalUnusedPenalty += unusedPenalty;
                unusedPenaltyRemnants++;
            }

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [OrToolsOptimizer] Unused stock penalty: {unusedPenaltyRemnants} remnants, " +
                $"total dynamic penalty={totalUnusedPenalty} (each active × UNUSED_STOCK_PENALTY_FACTOR={UNUSED_STOCK_PENALTY_FACTOR})");

            return LinearExpr.Sum(terms);
        }

        /// <summary>
        /// Извлекает решение из CP-SAT результата
        /// </summary>
        private OrToolsSolution ExtractSolution(
            CpSolver solver,
            CpSolverStatus status,
            Dictionary<(int, int), IntVar> patternVars,
            Dictionary<Guid, BoolVar> remnantUsedVars,
            long solveTimeMs)
        {
            var solution = new OrToolsSolution
            {
                FacadeId = _input.FacadeId,
                SolveTimeMs = solveTimeMs,
                Status = ConvertStatus(status)
            };

            if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
            {
                // Решение не найдено
                return solution;
            }

            solution.ObjectiveValue = solver.ObjectiveValue;

            // Извлекаем выбранные паттерны для каждого ряда
            foreach (var row in _input.Rows)
            {
                var rowSolution = new RowSolution
                {
                    RowIndex = row.RowIndex,
                    Height = row.Height,
                    Y = row.Y
                };

                foreach (var segment in row.Segments)
                {
                    var key = (row.RowIndex, segment.SegmentIndex);
                    if (!patternVars.ContainsKey(key))
                        continue;

                    int patternIndex = (int)solver.Value(patternVars[key]);
                    var patternsDict = GetPatterns();
                    var pattern = patternsDict[key][patternIndex];

                    rowSolution.Segments.Add(new SegmentSolution
                    {
                        SegmentIndex = segment.SegmentIndex,
                        StartX = segment.StartX,
                        EndX = segment.EndX,
                        Blocks = new List<Block>(pattern.Blocks)
                    });
                }

                solution.Rows.Add(rowSolution);
            }

            // Определяем оставшиеся остатки
            var usedRemnantIds = new HashSet<Guid>();
            foreach (var remnant in _input.StockRemnants)
            {
                if (solver.Value(remnantUsedVars[remnant.Id]) == 1)
                {
                    usedRemnantIds.Add(remnant.Id);
                }
            }

            // Неиспользованные остатки остаются на складе
            foreach (var remnant in _input.StockRemnants)
            {
                if (!usedRemnantIds.Contains(remnant.Id))
                {
                    solution.StockAfter.Add(new RemnantAfter
                    {
                        Id = remnant.Id,
                        Width = remnant.Width,
                        Height = remnant.Height,
                        Status = RemnantStatus.Unused
                    });
                }
            }

            // Добавляем созданные остатки (ширинные + высотные)
            foreach (var row in solution.Rows)
            {
                foreach (var segment in row.Segments)
                {
                    foreach (var block in segment.Blocks)
                    {
                        if (block.Type == BlockType.New)
                        {
                            // Width remnant
                            double leftoverW = _input.Constraints.TileWidth - block.Width;
                            if (leftoverW >= _input.Constraints.MinRemnantToSave)
                            {
                                solution.StockAfter.Add(new RemnantAfter
                                {
                                    Id = Guid.NewGuid(),
                                    Width = leftoverW,
                                    Height = row.Height,
                                    Status = RemnantStatus.CreatedFromNew,
                                    SourceBlockId = block.Id
                                });
                            }

                            // BUG-5 fix: Height remnant (when row is shorter than TileHeight)
                            if (row.Height < _input.Constraints.TileHeight - TOLERANCE)
                            {
                                double leftoverH = _input.Constraints.TileHeight - row.Height;
                                if (leftoverH >= _input.Constraints.MinRemnantToSave)
                                {
                                    solution.StockAfter.Add(new RemnantAfter
                                    {
                                        Id = Guid.NewGuid(),
                                        Width = _input.Constraints.TileWidth,
                                        Height = leftoverH,
                                        Status = RemnantStatus.CreatedFromNew,
                                        SourceBlockId = block.Id
                                    });
                                }
                            }
                        }
                        else if (block.Type == BlockType.CutRemnant)
                        {
                            double leftover = block.CutFrom - block.Width;
                            if (leftover >= _input.Constraints.MinRemnantToSave)
                            {
                                solution.StockAfter.Add(new RemnantAfter
                                {
                                    Id = Guid.NewGuid(),
                                    Width = leftover,
                                    Height = row.Height,
                                    Status = RemnantStatus.CreatedFromCut,
                                    SourceBlockId = block.Id
                                });
                            }
                        }
                    }
                }
            }

            // Вычисляем суммарную площадь остатков
            solution.TotalRemainingArea = solution.StockAfter.Sum(r => r.Width * r.Height) / 1_000_000.0; // мм² → м²

            return solution;
        }

        private SolverStatus ConvertStatus(CpSolverStatus status)
        {
            return status switch
            {
                CpSolverStatus.Optimal => SolverStatus.Optimal,
                CpSolverStatus.Feasible => SolverStatus.Feasible,
                CpSolverStatus.Infeasible => SolverStatus.Infeasible,
                CpSolverStatus.Unknown => SolverStatus.Timeout, // таймаут или прервано без решения
                _ => SolverStatus.Error
            };
        }
    }
}
