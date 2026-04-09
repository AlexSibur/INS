using System;
using System.Collections.Generic;
using System.Linq;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.OrTools
{
    /// <summary>
    /// Генератор допустимых паттернов раскладки блоков для каждого сегмента.
    /// Перебирает комбинации блоков (новые плиты + остатки) с учётом ограничений.
    /// </summary>
    public static class PatternGenerator
    {
        private const double TOLERANCE = 0.001;

        /// <summary>
        /// Генерирует все допустимые паттерны для всех сегментов фасада
        /// </summary>
        public static Dictionary<(int rowIndex, int segIndex), List<BlockPattern>> GeneratePatterns(
            FacadeInput input,
            OptimizerConfig config)
        {
            var result = new Dictionary<(int, int), List<BlockPattern>>();
            int globalPatternId = 0;

            // Уникальные ширины будущих сегментов для look-ahead эвристики K
            var futureWidths = input.FutureSegmentWidths ?? new List<double>();

            foreach (var row in input.Rows)
            {
                foreach (var segment in row.Segments)
                {
                    var patterns = GenerateSegmentPatterns(
                        segment,
                        row,
                        input.Windows,
                        input.StockRemnants,
                        input.Constraints,
                        config.MaxPatternsPerSegment,
                        ref globalPatternId,
                        futureWidths);

                    result[(row.RowIndex, segment.SegmentIndex)] = patterns;
                }
            }

            return result;
        }

        /// <summary>
        /// Генерирует паттерны для одного сегмента.
        /// Многофазная генерация:
        ///   Фаза 1 — ТОЛЬКО новые плиты (baseline joint-stagger паттерны, без остатков).
        ///            Цель: быстро создать базовые варианты перевязки швов для CP-SAT.
        ///   Фаза 2 — паттерны с остатками (BFD sweep, two-remnant combinations, start/end).
        ///   Фаза 3 — подстановка остатков в baseline паттерны.
        ///   Фаза 4 — look-ahead (heuristic K): резы под будущие сегменты.
        /// Приоритет остатков задаётся через CP-SAT objective (RemnantUsageWeight, STOCK_BONUS_MULTIPLIER),
        /// а не через порядок генерации фаз.
        /// Ограничение глубины: maxBlocks = ceil(width/tile) + 3 — исключает
        /// абсурдные комбинации из множества мелких кусков.
        /// </summary>
        private static List<BlockPattern> GenerateSegmentPatterns(
            RowSegment segment,
            RowDefinition row,
            List<WindowInfo> windows,
            List<Remnant> stockRemnants,
            OptimizationConstraints constraints,
            int maxPatterns,
            ref int globalPatternId,
            List<double> futureSegmentWidths = null)
        {
            var patterns = new List<BlockPattern>();
            var seen = new HashSet<string>(); // для дедупликации между фазами
            double segmentWidth = segment.Width;

            // BFD (Best Fit Decreasing): крупные остатки первыми — получают лучшие позиции
            var availableRemnants = stockRemnants
                .Where(r => CanUseRemnant(r, row.Height, constraints))
                .OrderByDescending(r => GetUsableWidth(r, row.Height))
                .ToList();

            // [NARROW-STRIP] диагностика: остатки, у которых usableWidth < rowHeight (используются повёрнутыми)
            var narrowStrips = availableRemnants
                .Where(r => GetUsableWidth(r, row.Height) < row.Height)
                .ToList();
            if (narrowStrips.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NARROW-STRIP] Row {row.RowIndex} Seg {segment.SegmentIndex} " +
                    $"(segW={segmentWidth:F0} rowH={row.Height:F0}): " +
                    $"{narrowStrips.Count} narrow remnant(s) in stock " +
                    $"[{string.Join(", ", narrowStrips.Take(5).Select(r => $"{r.Width:F0}x{r.Height:F0}(usw={GetUsableWidth(r, row.Height):F0})"))}]");
            }

            double minFirstBlock = segment.IsReleaseLeft ? constraints.MinReleasePiece : constraints.MinBlock;
            double minLastBlock = segment.IsReleaseRight ? constraints.MinReleasePiece : constraints.MinBlock;
            double minMiddleBlock = segment.NearWindowLeft || segment.NearWindowRight 
                ? constraints.MinBlockNearWindow 
                : constraints.MinBlock;

            // Максимальное число блоков в паттерне: минимум блоков + запас на перевязку
            int maxBlocks = (int)Math.Ceiling(segmentWidth / constraints.TileWidth) + 3;

            var currentBlocks = new List<Block>();
            var usedRemnantIds = new HashSet<Guid>();
            var emptyRemnants = new List<Remnant>(); // пустой список — рекурсия без остатков

            // ═══ Фаза 1: Baseline — ТОЛЬКО новые плиты (~10 паттернов) ═══
            // Создаёт joint-stagger baseline паттерны без остатков.
            // Остатки добавляются в Фазах 2–4 через хьюристики и подстановку.
            // Приоритет использования остатков определяется CP-SAT objective, не порядком фаз.
            GenerateRecursive(
                currentBlocks, usedRemnantIds, 0, segmentWidth,
                segment, row, emptyRemnants, constraints,
                patterns, seen, ref globalPatternId, maxPatterns,
                minFirstBlock, minMiddleBlock, minLastBlock,
                0, remnantFirst: false, maxBlocks: maxBlocks);

            // ═══ Фаза 2: Систематический перебор позиций для каждого остатка (шаг 10мм) ═══
            // Для каждого остатка пробуем позиции от 0 до segmentWidth-rw с шагом 10мм.
            // Предфильтр исключает заведомо невалидные позиции (prefix < minFirstBlock,
            // суффикс < minLastBlock) до вызова TryHeuristicPattern — экономит бюджет паттернов.
            // BFD-порядок: крупные остатки получают приоритет в бюджете паттернов.
            //
            // Бюджет Phase 2 = 2/3 от maxPatterns (был 1/3).
            // Увеличение гарантирует, что при складе 40+ остатков все они успевают пройти sweep,
            // а не только первые несколько крупнейших. Phase 3 по-прежнему получает остаток бюджета.
            const double SWEEP_STEP = 10.0;
            int phase1PatternCount = patterns.Count; // количество паттернов после Phase 1
            int phase2SingleBudget = phase1PatternCount + Math.Max(150, maxPatterns / 2);
            int phase2BudgetCap = phase2SingleBudget;

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [PatternGenerator] " +
                $"phase1_budget={phase1PatternCount} phase2_budget={phase2BudgetCap - phase1PatternCount} " +
                $"remnants_to_sweep={availableRemnants.Count}");

            int phase2StartCount = patterns.Count;
            int remnantsSwept = 0;
            foreach (var remnant in availableRemnants)
            {
                if (patterns.Count >= phase2BudgetCap) break;
                double rw = GetUsableWidth(remnant, row.Height);
                if (rw < constraints.MinBlock) continue;

                remnantsSwept++;
                for (double offset = 0; offset <= segmentWidth - rw + TOLERANCE; offset += SWEEP_STEP)
                {
                    if (patterns.Count >= phase2BudgetCap) break;

                    // Предфильтр 1: prefix-блок должен быть >= minFirstBlock (или отсутствовать)
                    if (offset > TOLERANCE && offset < minFirstBlock - TOLERANCE) continue;

                    // Предфильтр 2: суффикс после остатка — последний кусочек не должен быть < minLastBlock
                    double suffix = segmentWidth - offset - rw;
                    if (suffix > TOLERANCE)
                    {
                        double lastChunk = suffix % constraints.TileWidth;
                        if (lastChunk < TOLERANCE) lastChunk = constraints.TileWidth; // точно кратно
                        if (lastChunk > TOLERANCE && lastChunk < minLastBlock - TOLERANCE) continue;
                    }

                    TryHeuristicPattern(remnant, rw, offset, segmentWidth, segment, row,
                        constraints, patterns, seen, ref globalPatternId, minFirstBlock, minLastBlock);
                }

                // Явно добавляем вариант "остаток последним блоком" (точная подгонка под конец)
                if (patterns.Count < phase2BudgetCap)
                    TryHeuristicPatternRemnantLast(remnant, rw, segmentWidth, segment, row,
                        constraints, patterns, seen, ref globalPatternId, minFirstBlock, minLastBlock);
            }

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [PatternGenerator] phase2 swept {remnantsSwept}/{availableRemnants.Count} " +
                $"remnant positions, patterns_added={patterns.Count - phase2StartCount}");

            // [NARROW-STRIP] log: сколько паттернов было сгенерировано с участием узких полос
            if (narrowStrips.Count > 0)
            {
                var narrowIds = new HashSet<Guid>(narrowStrips.Select(r => r.Id));
                int narrowPatterns = patterns
                    .Skip(phase1PatternCount)
                    .Count(p => p.UsedRemnantIds.Any(id => narrowIds.Contains(id)));
                System.Diagnostics.Debug.WriteLine(
                    $"[NARROW-STRIP] Row {row.RowIndex} Seg {segment.SegmentIndex}: " +
                    $"generated {narrowPatterns} patterns using narrow strip(s)");
            }

            // ═══ Фаза 2b: Два остатка в одном сегменте — sweep первого, второй вплотную за ним ═══
            // R1 перебирается с шагом 10мм от начала до конца сегмента.
            // R2 размещается сразу после R1 (вплотную). Это покрывает все позиции пары без O(N²×len²) взрыва.
            // Дополнительно: классический вариант R1-начало/R2-конец для разделённых пар.
            if (availableRemnants.Count >= 2 && patterns.Count < maxPatterns)
            {
                int phase2bTopN = Math.Min(availableRemnants.Count, Math.Max(20, availableRemnants.Count / 3));
                var topRemnants = availableRemnants
                    .OrderByDescending(r => GetUsableWidth(r, row.Height))
                    .Take(phase2bTopN)
                    .ToList();

                for (int i = 0; i < topRemnants.Count && patterns.Count < maxPatterns; i++)
                {
                    var r1 = topRemnants[i];
                    double rw1 = GetUsableWidth(r1, row.Height);
                    if (rw1 < constraints.MinBlock) continue;

                    for (int j = i + 1; j < topRemnants.Count && patterns.Count < maxPatterns; j++)
                    {
                        var r2 = topRemnants[j];
                        double rw2 = GetUsableWidth(r2, row.Height);
                        if (rw2 < constraints.MinBlock) continue;
                        if (rw1 + rw2 > segmentWidth + TOLERANCE) continue;

                        // Sweep: R1 на offset, R2 сразу за R1 — пара может оказаться в любом месте ряда
                        for (double offset = 0;
                             offset <= segmentWidth - rw1 - rw2 + TOLERANCE && patterns.Count < maxPatterns;
                             offset += SWEEP_STEP)
                        {
                            TryTwoRemnantsAtOffset(r1, rw1, r2, rw2, offset, segmentWidth, segment, row,
                                constraints, patterns, seen, ref globalPatternId, minFirstBlock, minLastBlock);
                        }

                        // Классика: R1 в начале, R2 в конце (разделённые остатки)
                        if (patterns.Count < maxPatterns)
                            TryTwoRemnantsStartEnd(r1, rw1, r2, rw2, segmentWidth, segment, row,
                                constraints, patterns, seen, ref globalPatternId, minFirstBlock, minLastBlock);
                    }
                }
            }
            // ═══ Фаза 2c: Чередование остатков (≥3 остатков в ряду) ═══
            if (availableRemnants.Count >= 3 && patterns.Count < maxPatterns)
            {
                int phase2cTopN = Math.Min(availableRemnants.Count, Math.Max(25, availableRemnants.Count / 3));
                var topRemnants = availableRemnants
                    .OrderByDescending(r => GetUsableWidth(r, row.Height))
                    .Take(phase2cTopN)
                    .ToList();
                
                var currentBlocksForAlt = new List<Block>();
                var usedIdsForAlt = new HashSet<Guid>();
                TryAlternatingRemnantsRecursive(
                    currentBlocksForAlt, usedIdsForAlt, 0, segmentWidth,
                    segment, row, topRemnants, constraints,
                    patterns, seen, ref globalPatternId, maxPatterns,
                    minFirstBlock, minLastBlock);
            }

            // ═══ Фаза 2d: Чередование ПАР остатков (R+R)+T+(R+R)+T+... ═══
            // Покрывает паттерн, где MaxConsecutiveRemnants=2, а пары разделены целыми плитами.
            // Пример на 15770мм фасаде: R(600)+R(400)+T(1200)+R(500)+R(700)+T(1200)+R(300)+R(900)...
            // Phase 2c покрывает R→T→R→T (одиночные), но не R+R→T→R+R→T (пары).
            if (availableRemnants.Count >= 4 && patterns.Count < maxPatterns)
            {
                int phase2dTopN = Math.Min(availableRemnants.Count, Math.Max(20, availableRemnants.Count / 3));
                var topRemnantsForPair = availableRemnants
                    .OrderByDescending(r => GetUsableWidth(r, row.Height))
                    .Take(phase2dTopN)
                    .ToList();

                var currentBlocksForPair = new List<Block>();
                var usedIdsForPair = new HashSet<Guid>();
                TryPairAlternatingRecursive(
                    currentBlocksForPair, usedIdsForPair, 0, segmentWidth,
                    segment, row, topRemnantsForPair, constraints,
                    patterns, seen, ref globalPatternId, maxPatterns,
                    minFirstBlock, minLastBlock,
                    pairPhase: 0); // 0=начало пары, 1=второй в паре
            }

            // ═══ Фаза 3: Подстановка остатков вместо обрезанных новых плит (Эвристика J) ═══
            // Берём паттерны Фазы 1 (только новые плиты) и заменяем частичную плиту на подходящий остаток.
            // Самый точный способ утилизации: остаток занимает ровно тот размер, который иначе был бы обрезкой.
            if (availableRemnants.Count > 0 && patterns.Count < maxPatterns)
            {
                // Phase 1 patterns are at the start of the list (UsedRemnantIds.Count == 0)
                int phase1Count = 0;
                while (phase1Count < patterns.Count && patterns[phase1Count].UsedRemnantIds.Count == 0)
                    phase1Count++;

                for (int pi = 0; pi < phase1Count && patterns.Count < maxPatterns; pi++)
                {
                    var basePattern = patterns[pi];
                    for (int bi = 0; bi < basePattern.Blocks.Count && patterns.Count < maxPatterns; bi++)
                    {
                        var block = basePattern.Blocks[bi];
                        if (block.Type != BlockType.New) continue;
                        if (Math.Abs(block.Width - constraints.TileWidth) < TOLERANCE) continue; // полная плита — пропуск

                        foreach (var remnant in availableRemnants)
                        {
                            if (patterns.Count >= maxPatterns) break;
                            double rw2 = GetUsableWidth(remnant, row.Height);
                            if (rw2 < block.Width - TOLERANCE) continue; // остаток меньше нужного
                            if (rw2 < constraints.MinBlock) continue;

                            TrySubstituteRemnant(basePattern, bi, remnant, rw2, segment, row,
                                constraints, patterns, seen, ref globalPatternId);
                        }
                    }
                }
            }

            // ═══ Фаза 4: Look-ahead — Эвристика K (разрезы под будущие сегменты) ═══
            // Для каждой уникальной ширины будущего сегмента: создаём паттерн,
            // где первый блок (обрезанный New) имеет ширину = segmentWidth - futureWidth,
            // чтобы остаток от резки новой плиты = futureWidth (пригодится в будущем).
            if (futureSegmentWidths != null && futureSegmentWidths.Count > 0 && patterns.Count < maxPatterns)
            {
                var uniqueFutureWidths = futureSegmentWidths
                    .Where(fw => fw > constraints.MinRemnantToSave && fw < constraints.TileWidth - TOLERANCE)
                    .Distinct()
                    .OrderByDescending(w => w) // крупные сначала — ценнее
                    .Take(10); // ограничиваем кол-во

                foreach (double futureW in uniqueFutureWidths)
                {
                    if (patterns.Count >= maxPatterns) break;

                    // Идеальная ширина первого блока: TileWidth - futureW → остаток = futureW
                    double idealFirstBlock = constraints.TileWidth - futureW;
                    if (idealFirstBlock < minFirstBlock || idealFirstBlock > constraints.TileWidth - TOLERANCE)
                        continue;

                    // Проверяем что это создаёт осмысленный паттерн
                    if (idealFirstBlock + minLastBlock > segmentWidth + TOLERANCE)
                        continue;

                    TryLookAheadPattern(idealFirstBlock, segmentWidth, segment, row,
                        constraints, patterns, seen, ref globalPatternId, minFirstBlock, minLastBlock);
                }
            }

            // ═══ Валидация CornerZone (ТЗ v11) ═══
            // Стыки не должны попадать в зону ±CornerZone мм от края нахлеста.
            // FIX: Проверяем ВСЕ окна по X-координате, а не только пересекающие ряд по Y.
            // CornerZone — ограничение по X: стыки рядом с overlap edge создают слабые зоны
            // даже в рядах выше/ниже окна (через перевязку швов с оконными рядами).
            if (patterns.Count > 0 && windows != null && windows.Count > 0)
            {
                var validPatterns = new List<BlockPattern>(patterns.Count);
                double minCornerZone = constraints.CornerZone;
                
                // Pre-compute overlap edges for all windows
                var allOverlapEdges = new List<double>(windows.Count * 2);
                foreach (var w in windows)
                {
                    allOverlapEdges.Add(w.MinX + constraints.WindowOverlap);
                    allOverlapEdges.Add(w.MaxX - constraints.WindowOverlap);
                }
                
                foreach (var pattern in patterns)
                {
                    bool isValid = true;
                    if (pattern.JointPositions.Count > 0)
                    {
                        foreach (double jointX in pattern.JointPositions)
                        {
                            foreach (double edge in allOverlapEdges)
                            {
                                if (Math.Abs(jointX - edge) < minCornerZone - TOLERANCE)
                                {
                                    isValid = false;
                                    break;
                                }
                            }
                            if (!isValid) break;
                        }
                    }
                    if (isValid)
                    {
                        validPatterns.Add(pattern);
                    }
                }
                patterns = validPatterns;
            }

#if DEBUG
            // [FRESH] Диагностика: сколько финальных паттернов используют свежие остатки
            {
                var freshRemnantIds = new HashSet<Guid>(
                    availableRemnants.Where(r => r.CreatedAtRow >= 0 && r.CreatedAtRow >= row.RowIndex - 2)
                                     .Select(r => r.Id));
                if (freshRemnantIds.Count > 0)
                {
                    int freshPatterns = patterns.Count(p => p.UsedRemnantIds.Any(id => freshRemnantIds.Contains(id)));
                    System.Diagnostics.Debug.WriteLine(
                        $"[FRESH] Row {row.RowIndex} Seg {segment.SegmentIndex}: " +
                        $"{freshRemnantIds.Count} fresh remnant(s) → {freshPatterns} patterns generated");
                }
            }
#endif

            return patterns;
        }

        /// <summary>
        /// Точечная эвристика: создаёт один паттерн, помещая остаток после newPrefixWidth новой плиты,
        /// а оставшееся пространство заполняет целыми новыми плитами.
        /// </summary>
        private static void TryHeuristicPattern(
            Remnant remnant, double remnantWidth,
            double newPrefixWidth, double segmentWidth,
            RowSegment segment, RowDefinition row,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId,
            double minFirstBlock, double minLastBlock)
        {
            var blocks = new List<Block>();
            double x = 0;

            // Первый блок(и): новые плиты (разбиваем на куски ≤ TileWidth)
            if (newPrefixWidth > TOLERANCE)
            {
                double prefixRemaining = newPrefixWidth;
                while (prefixRemaining > TOLERANCE)
                {
                    double w = Math.Min(constraints.TileWidth, prefixRemaining);
                    blocks.Add(new Block
                    {
                        X = segment.StartX + x,
                        Width = w,
                        Type = BlockType.New
                    });
                    x += w;
                    prefixRemaining -= w;
                }
            }

            // Остаток (целиком или обрезанный)
            double remaining = segmentWidth - x;
            double rUse = Math.Min(remnantWidth, remaining);
            if (rUse < minFirstBlock) return; // слишком маленький
            double afterRemnant = remaining - rUse;
            if (afterRemnant > TOLERANCE && afterRemnant < minLastBlock)
            {
                rUse = remaining - minLastBlock;
                if (rUse < minFirstBlock) return;
            }

            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = rUse,
                Type = rUse < remnantWidth - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = remnant.Id,
                CutFrom = remnantWidth,
                IsRotated = remnant.Height < row.Height && remnant.Width >= row.Height
            });
            x += rUse;

            // Заполняем оставшееся целыми новыми плитами
            remaining = segmentWidth - x;
            while (remaining > TOLERANCE)
            {
                double tileW = Math.Min(constraints.TileWidth, remaining);
                double afterTile = remaining - tileW;
                if (afterTile > TOLERANCE && afterTile < minLastBlock)
                {
                    // Расширяем только если не превышаем TileWidth
                    if (remaining <= constraints.TileWidth + TOLERANCE)
                        tileW = remaining;
                    // Иначе оставляем tileW = TileWidth, остаток обработается следующей итерацией
                }

                if (tileW < minLastBlock && blocks.Count > 0)
                {
                    // Слишком маленький кусок — расширяем предыдущий блок
                    var last = blocks[blocks.Count - 1];
                    if (last.Type == BlockType.New && last.Width + remaining <= constraints.TileWidth + TOLERANCE)
                    {
                        blocks[blocks.Count - 1] = new Block
                        {
                            X = last.X, Width = last.Width + remaining, Type = BlockType.New
                        };
                        remaining = 0;
                        break;
                    }
                    break; // не удалось — паттерн невалидный
                }

                blocks.Add(new Block
                {
                    X = segment.StartX + x,
                    Width = tileW,
                    Type = BlockType.New
                });
                x += tileW;
                remaining = segmentWidth - x;
            }

            // Проверяем что заполнили точно
            double total = blocks.Sum(b => b.Width);
            if (Math.Abs(total - segmentWidth) > TOLERANCE) return;

            string key = PatternKey(blocks);
            if (seen.Add(key))
            {
                var usedIds = new HashSet<Guid> { remnant.Id };
                var pattern = CreatePattern(blocks, segment, row, usedIds, ref globalPatternId, constraints);
                patterns.Add(pattern);
            }
        }

        /// <summary>
        /// Точечная эвристика: заполняет сегмент новыми плитами, последний блок — остаток.
        /// </summary>
        private static void TryHeuristicPatternRemnantLast(
            Remnant remnant, double remnantWidth,
            double segmentWidth,
            RowSegment segment, RowDefinition row,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId,
            double minFirstBlock, double minLastBlock)
        {
            var blocks = new List<Block>();
            double targetNewWidth = segmentWidth - remnantWidth;
            if (targetNewWidth < minFirstBlock) return;

            double x = 0;
            double newRemaining = targetNewWidth;

            // Заполняем новыми плитами до места для остатка
            while (newRemaining > TOLERANCE)
            {
                double tileW = Math.Min(constraints.TileWidth, newRemaining);
                if (tileW < minFirstBlock) return; // невалидно

                blocks.Add(new Block
                {
                    X = segment.StartX + x,
                    Width = tileW,
                    Type = BlockType.New
                });
                x += tileW;
                newRemaining = targetNewWidth - x;
            }

            // Последний блок — остаток
            double rUse = segmentWidth - x;
            if (rUse < constraints.MinBlock || rUse > remnantWidth + TOLERANCE) return;

            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = rUse,
                Type = rUse < remnantWidth - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = remnant.Id,
                CutFrom = remnantWidth,
                IsRotated = remnant.Height < row.Height && remnant.Width >= row.Height
            });

            double total = blocks.Sum(b => b.Width);
            if (Math.Abs(total - segmentWidth) > TOLERANCE) return;

            string key = PatternKey(blocks);
            if (seen.Add(key))
            {
                var usedIds = new HashSet<Guid> { remnant.Id };
                var pattern = CreatePattern(blocks, segment, row, usedIds, ref globalPatternId, constraints);
                patterns.Add(pattern);
            }
        }

        /// <summary>
        /// Два остатка подряд в начале сегмента (R1, R2), затем новые плиты.
        /// Допустимо: MaxConsecutiveRemnants = 2.
        /// </summary>
        private static void TryTwoRemnantsAdjacent(
            Remnant r1, double rw1, Remnant r2, double rw2,
            double segmentWidth, RowSegment segment, RowDefinition row,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId,
            double minFirstBlock, double minLastBlock)
        {
            if (rw1 < minFirstBlock) return;

            var blocks = new List<Block>();
            double x = 0;

            // R1 в начале
            double use1 = Math.Min(rw1, segmentWidth);
            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = use1,
                Type = use1 < rw1 - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = r1.Id,
                CutFrom = rw1,
                IsRotated = r1.Height < row.Height && r1.Width >= row.Height
            });
            x += use1;

            // R2 сразу после R1
            double remaining = segmentWidth - x;
            double use2 = Math.Min(rw2, remaining);
            if (use2 < constraints.MinBlock) return;
            double afterR2 = remaining - use2;
            if (afterR2 > TOLERANCE && afterR2 < minLastBlock)
            {
                use2 = remaining - minLastBlock;
                if (use2 < constraints.MinBlock) return;
            }
            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = use2,
                Type = use2 < rw2 - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = r2.Id,
                CutFrom = rw2,
                IsRotated = r2.Height < row.Height && r2.Width >= row.Height
            });
            x += use2;

            // Заполняем оставшееся новыми плитами
            remaining = segmentWidth - x;
            while (remaining > TOLERANCE)
            {
                double tileW = Math.Min(constraints.TileWidth, remaining);
                double afterTile = remaining - tileW;
                if (afterTile > TOLERANCE && afterTile < minLastBlock)
                {
                    if (remaining <= constraints.TileWidth + TOLERANCE)
                        tileW = remaining;
                }

                if (tileW < minLastBlock && blocks.Count > 0)
                {
                    var last = blocks[blocks.Count - 1];
                    if (last.Type == BlockType.New && last.Width + remaining <= constraints.TileWidth + TOLERANCE)
                    {
                        blocks[blocks.Count - 1] = new Block
                        {
                            X = last.X, Width = last.Width + remaining, Type = BlockType.New
                        };
                        remaining = 0;
                        break;
                    }
                    return; // невалидный паттерн
                }

                blocks.Add(new Block
                {
                    X = segment.StartX + x,
                    Width = tileW,
                    Type = BlockType.New
                });
                x += tileW;
                remaining = segmentWidth - x;
            }

            double total = blocks.Sum(b => b.Width);
            if (Math.Abs(total - segmentWidth) > TOLERANCE) return;

            string key = PatternKey(blocks);
            if (seen.Add(key))
            {
                var usedIds = new HashSet<Guid> { r1.Id, r2.Id };
                var pattern = CreatePattern(blocks, segment, row, usedIds, ref globalPatternId, constraints);
                patterns.Add(pattern);
            }
        }

        /// <summary>
        /// Один остаток в начале, новые плиты посередине, второй остаток в конце.
        /// Максимальная утилизация склада без нарушения MaxConsecutiveRemnants.
        /// </summary>
        private static void TryTwoRemnantsStartEnd(
            Remnant r1, double rw1, Remnant r2, double rw2,
            double segmentWidth, RowSegment segment, RowDefinition row,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId,
            double minFirstBlock, double minLastBlock)
        {
            if (rw1 < minFirstBlock) return;

            double middleSpace = segmentWidth - rw1 - rw2;
            if (middleSpace < -TOLERANCE) return; // не помещаются
            // Если середина слишком мала для плиты, но > 0 — невалидно
            if (middleSpace > TOLERANCE && middleSpace < constraints.MinBlock) return;

            var blocks = new List<Block>();
            double x = 0;

            // R1 в начале (полностью)
            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = rw1,
                Type = BlockType.Remnant,
                RemnantId = r1.Id,
                CutFrom = rw1,
                IsRotated = r1.Height < row.Height && r1.Width >= row.Height
            });
            x += rw1;

            // Новые плиты в середине
            double midRemaining = segmentWidth - x - rw2;
            while (midRemaining > TOLERANCE)
            {
                double tileW = Math.Min(constraints.TileWidth, midRemaining);
                if (tileW < constraints.MinBlock) return; // невалидный кусок

                blocks.Add(new Block
                {
                    X = segment.StartX + x,
                    Width = tileW,
                    Type = BlockType.New
                });
                x += tileW;
                midRemaining = segmentWidth - x - rw2;
            }

            // R2 в конце
            double use2 = segmentWidth - x;
            if (use2 < minLastBlock || use2 > rw2 + TOLERANCE) return;
            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = use2,
                Type = use2 < rw2 - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = r2.Id,
                CutFrom = rw2,
                IsRotated = r2.Height < row.Height && r2.Width >= row.Height
            });

            double total = blocks.Sum(b => b.Width);
            if (Math.Abs(total - segmentWidth) > TOLERANCE) return;

            string key = PatternKey(blocks);
            if (seen.Add(key))
            {
                var usedIds = new HashSet<Guid> { r1.Id, r2.Id };
                var pattern = CreatePattern(blocks, segment, row, usedIds, ref globalPatternId, constraints);
                patterns.Add(pattern);
            }
        }

        /// <summary>
        /// Sweep-вариант для двух остатков: новые плиты (prefixWidth) → R1 → R2 → новые плиты.
        /// Вызывается из Phase 2b для каждой позиции offset с шагом 10мм.
        /// </summary>
        private static void TryTwoRemnantsAtOffset(
            Remnant r1, double rw1, Remnant r2, double rw2,
            double prefixWidth, double segmentWidth,
            RowSegment segment, RowDefinition row,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId,
            double minFirstBlock, double minLastBlock)
        {
            var blocks = new List<Block>();
            double x = 0;

            // Префикс: новые плиты перед R1
            if (prefixWidth > TOLERANCE)
            {
                double prem = prefixWidth;
                while (prem > TOLERANCE)
                {
                    double w = Math.Min(constraints.TileWidth, prem);
                    blocks.Add(new Block { X = segment.StartX + x, Width = w, Type = BlockType.New });
                    x += w;
                    prem -= w;
                }
            }

            // R1
            double use1 = Math.Min(rw1, segmentWidth - x);
            if (use1 < minFirstBlock && blocks.Count == 0) return;
            if (use1 < constraints.MinBlock) return;
            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = use1,
                Type = use1 < rw1 - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = r1.Id,
                CutFrom = rw1,
                IsRotated = r1.Height < row.Height && r1.Width >= row.Height
            });
            x += use1;

            // R2 вплотную за R1
            double remaining = segmentWidth - x;
            double use2 = Math.Min(rw2, remaining);
            if (use2 < constraints.MinBlock) return;
            double afterR2 = remaining - use2;
            if (afterR2 > TOLERANCE && afterR2 < minLastBlock)
            {
                use2 = remaining - minLastBlock;
                if (use2 < constraints.MinBlock) return;
            }
            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = use2,
                Type = use2 < rw2 - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = r2.Id,
                CutFrom = rw2,
                IsRotated = r2.Height < row.Height && r2.Width >= row.Height
            });
            x += use2;

            // Суффикс: новые плиты после R2
            remaining = segmentWidth - x;
            while (remaining > TOLERANCE)
            {
                double tileW = Math.Min(constraints.TileWidth, remaining);
                double afterTile = remaining - tileW;
                if (afterTile > TOLERANCE && afterTile < minLastBlock)
                {
                    if (remaining <= constraints.TileWidth + TOLERANCE)
                        tileW = remaining;
                }
                if (tileW < minLastBlock && blocks.Count > 0)
                {
                    var last = blocks[blocks.Count - 1];
                    if (last.Type == BlockType.New && last.Width + remaining <= constraints.TileWidth + TOLERANCE)
                    {
                        blocks[blocks.Count - 1] = new Block
                            { X = last.X, Width = last.Width + remaining, Type = BlockType.New };
                        remaining = 0;
                        break;
                    }
                    return;
                }
                blocks.Add(new Block { X = segment.StartX + x, Width = tileW, Type = BlockType.New });
                x += tileW;
                remaining = segmentWidth - x;
            }

            double total = blocks.Sum(b => b.Width);
            if (Math.Abs(total - segmentWidth) > TOLERANCE) return;

            string key = PatternKey(blocks);
            if (seen.Add(key))
            {
                var usedIds = new HashSet<Guid> { r1.Id, r2.Id };
                var pattern = CreatePattern(blocks, segment, row, usedIds, ref globalPatternId, constraints);
                patterns.Add(pattern);
            }
        }

        /// <summary>
        /// Подстановка: берёт существующий Phase-1 паттерн (только новые плиты) и заменяет
        /// один обрезанный New-блок на подходящий остаток такого же или большего размера.
        /// Самый точный способ утилизации остатков: остаток занимает ровно то место,
        /// которое иначе заняла бы обрезанная новая плита (создав отход).
        /// </summary>
        private static void TrySubstituteRemnant(
            BlockPattern basePattern, int blockIndex,
            Remnant remnant, double remnantWidth,
            RowSegment segment, RowDefinition row,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId)
        {
            var origBlock = basePattern.Blocks[blockIndex];
            double useWidth = origBlock.Width;
            if (useWidth < constraints.MinBlock) return;

            // Если остаток уже используется в этом паттерне — пропуск
            if (basePattern.UsedRemnantIds.Contains(remnant.Id)) return;

            var newBlocks = new List<Block>(basePattern.Blocks);
            newBlocks[blockIndex] = new Block
            {
                X = origBlock.X,
                Width = useWidth,
                Type = useWidth < remnantWidth - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                RemnantId = remnant.Id,
                CutFrom = remnantWidth,
                IsRotated = remnant.Height < row.Height && remnant.Width >= row.Height
            };

            string key = PatternKey(newBlocks);
            if (seen.Add(key))
            {
                var usedIds = new HashSet<Guid>(basePattern.UsedRemnantIds) { remnant.Id };
                var pattern = CreatePattern(newBlocks, segment, row, usedIds, ref globalPatternId, constraints);
                patterns.Add(pattern);
            }
        }

        /// <summary>
        /// Фаза 2d: Рекурсивное чередование ПАР остатков и новых плит.
        /// Строит паттерны вида (R₁+R₂) → T → (R₃+R₄) → T → (R₅+R₆).
        /// Вызывается с pairPhase=0 (начало пары) или pairPhase=1 (второй остаток в паре).
        /// MaxConsecutiveRemnants=2 соблюдается структурой: после 2 остатков ВСЕГДА идёт новая плита.
        /// </summary>
        private static void TryPairAlternatingRecursive(
            List<Block> currentBlocks, HashSet<Guid> usedRemnantIds,
            double currentX, double segmentWidth, RowSegment segment, RowDefinition row,
            List<Remnant> topRemnants, OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId, int maxPatterns,
            double minFirstBlock, double minLastBlock,
            int pairPhase) // 0=первый в паре, 1=второй в паре
        {
            if (patterns.Count >= maxPatterns) return;
            if (currentBlocks.Count > 20) return; // защита от глубокой рекурсии

            double remaining = segmentWidth - currentX;

            // База: сегмент заполнен
            if (Math.Abs(remaining) < TOLERANCE)
            {
                // Принимаем только паттерны с ≥4 остатками (иначе 2c уже покрыл)
                if (usedRemnantIds.Count >= 4)
                {
                    string key = PatternKey(currentBlocks);
                    if (seen.Add(key))
                    {
                        var pattern = CreatePattern(currentBlocks, segment, row, usedRemnantIds, ref globalPatternId, constraints);
                        patterns.Add(pattern);
                    }
                }
                return;
            }

            if (remaining < constraints.MinBlock) return;

            bool isFirstBlock = currentBlocks.Count == 0;
            double minWidth = isFirstBlock ? minFirstBlock : constraints.MinBlock;

            if (pairPhase == 0)
            {
                // === Шаг A: добавить первый остаток пары ===
                foreach (var remnant in topRemnants)
                {
                    if (patterns.Count >= maxPatterns) break;
                    if (usedRemnantIds.Contains(remnant.Id)) continue;
                    double rw = GetUsableWidth(remnant, row.Height);
                    if (rw < minWidth) continue;

                    double useW = Math.Min(rw, remaining);
                    if (useW < minWidth) continue;
                    double leftAfter = remaining - useW;
                    // Проверка: после первого остатка должно хватить места хотя бы на MinBlock
                    if (leftAfter > TOLERANCE && leftAfter < minLastBlock) continue;

                    usedRemnantIds.Add(remnant.Id);
                    currentBlocks.Add(new Block
                    {
                        X = segment.StartX + currentX, Width = useW,
                        Type = useW < rw - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                        RemnantId = remnant.Id, CutFrom = rw,
                        IsRotated = remnant.Height < row.Height && remnant.Width >= row.Height
                    });

                    // Переходим к фазе 1: добавить второй остаток пары
                    TryPairAlternatingRecursive(
                        currentBlocks, usedRemnantIds, currentX + useW, segmentWidth,
                        segment, row, topRemnants, constraints,
                        patterns, seen, ref globalPatternId, maxPatterns,
                        minFirstBlock, minLastBlock, pairPhase: 1);

                    currentBlocks.RemoveAt(currentBlocks.Count - 1);
                    usedRemnantIds.Remove(remnant.Id);
                }
            }
            else // pairPhase == 1
            {
                // === Шаг B: добавить второй остаток пары (или завершить паттерн без него) ===

                // Вариант B1: добавить второй остаток, затем целую плиту
                if (usedRemnantIds.Count < topRemnants.Count)
                {
                    foreach (var remnant in topRemnants)
                    {
                        if (patterns.Count >= maxPatterns) break;
                        if (usedRemnantIds.Contains(remnant.Id)) continue;
                        double rw = GetUsableWidth(remnant, row.Height);
                        if (rw < constraints.MinBlock) continue;

                        double useW = Math.Min(rw, remaining);
                        if (useW < constraints.MinBlock) continue;
                        double afterR2 = remaining - useW;
                        if (afterR2 > TOLERANCE && afterR2 < minLastBlock) continue;

                        usedRemnantIds.Add(remnant.Id);
                        currentBlocks.Add(new Block
                        {
                            X = segment.StartX + currentX, Width = useW,
                            Type = useW < rw - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                            RemnantId = remnant.Id, CutFrom = rw,
                            IsRotated = remnant.Height < row.Height && remnant.Width >= row.Height
                        });
                        double xAfterR2 = currentX + useW;
                        double remAfterR2 = segmentWidth - xAfterR2;

                        if (Math.Abs(remAfterR2) < TOLERANCE)
                        {
                            // Пара заполнила сегмент — принимаем
                            if (usedRemnantIds.Count >= 4)
                            {
                                string key = PatternKey(currentBlocks);
                                if (seen.Add(key))
                                {
                                    var p = CreatePattern(currentBlocks, segment, row, usedRemnantIds, ref globalPatternId, constraints);
                                    patterns.Add(p);
                                }
                            }
                        }
                        else if (remAfterR2 >= constraints.TileWidth - TOLERANCE)
                        {
                            // Добавляем целую плиту как разделитель → следующая пара
                            double tileW = Math.Min(constraints.TileWidth, remAfterR2);
                            double afterTile = remAfterR2 - tileW;
                            if (afterTile < TOLERANCE || afterTile >= minLastBlock)
                            {
                                currentBlocks.Add(new Block
                                {
                                    X = segment.StartX + xAfterR2,
                                    Width = tileW,
                                    Type = BlockType.New
                                });

                                TryPairAlternatingRecursive(
                                    currentBlocks, usedRemnantIds, xAfterR2 + tileW, segmentWidth,
                                    segment, row, topRemnants, constraints,
                                    patterns, seen, ref globalPatternId, maxPatterns,
                                    minFirstBlock, minLastBlock, pairPhase: 0);

                                currentBlocks.RemoveAt(currentBlocks.Count - 1);
                            }
                        }

                        currentBlocks.RemoveAt(currentBlocks.Count - 1);
                        usedRemnantIds.Remove(remnant.Id);
                    }
                }

                // Вариант B2: закончить пару одним остатком (без второго) → целая плита
                // Полезно когда оставшееся место только для одного R + T
                double remNow = segmentWidth - currentX;
                if (remNow >= constraints.TileWidth + constraints.MinBlock)
                {
                    double tileW = constraints.TileWidth;
                    currentBlocks.Add(new Block
                    {
                        X = segment.StartX + currentX,
                        Width = tileW,
                        Type = BlockType.New
                    });
                    TryPairAlternatingRecursive(
                        currentBlocks, usedRemnantIds, currentX + tileW, segmentWidth,
                        segment, row, topRemnants, constraints,
                        patterns, seen, ref globalPatternId, maxPatterns,
                        minFirstBlock, minLastBlock, pairPhase: 0);
                    currentBlocks.RemoveAt(currentBlocks.Count - 1);
                }
            }
        }

        /// <summary>
        /// Эвристика C: Рекурсивное чередование остатков и новых плит (для >2 остатков в сегменте).
        /// Строит паттерны вида Остаток -> Новая -> Остаток -> Новая -> Остаток.
        /// Точечная функция без взрыва комбинаторики.
        /// </summary>
        private static void TryAlternatingRemnantsRecursive(
            List<Block> currentBlocks, HashSet<Guid> usedRemnantIds,
            double currentX, double segmentWidth, RowSegment segment, RowDefinition row,
            List<Remnant> topRemnants, OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId, int maxPatterns,
            double minFirstBlock, double minLastBlock)
        {
            if (patterns.Count >= maxPatterns) return;
            if (currentBlocks.Count > 15) return; // Защита от бесконечной рекурсии
            
            double remaining = segmentWidth - currentX;

            if (Math.Abs(remaining) < TOLERANCE)
            {
                if (usedRemnantIds.Count >= 3) // Нас интересуют только комбинации из >= 3 остатков
                {
                    string key = PatternKey(currentBlocks);
                    if (seen.Add(key))
                    {
                        var pattern = CreatePattern(currentBlocks, segment, row, usedRemnantIds, ref globalPatternId, constraints);
                        patterns.Add(pattern);
                    }
                }
                return;
            }

            if (remaining < constraints.MinBlock) return;

            bool isFirstBlock = currentBlocks.Count == 0;
            double minWidth = isFirstBlock ? minFirstBlock : constraints.MinBlock;

            // 1. Ветка: Добавить Новую плиту (как разделитель)
            if (!isFirstBlock && currentBlocks[currentBlocks.Count - 1].Type != BlockType.New)
            {
                double maxNewTileW = Math.Min(constraints.TileWidth, remaining);
                var options = new List<double> { maxNewTileW };
                
                foreach (var w in options)
                {
                    if (w < minWidth) continue;
                    
                    double leftAfter = remaining - w;
                    if (leftAfter > TOLERANCE && leftAfter < minLastBlock)
                        continue;
                    
                    currentBlocks.Add(new Block { X = segment.StartX + currentX, Width = w, Type = BlockType.New });
                    TryAlternatingRemnantsRecursive(
                        currentBlocks, usedRemnantIds, currentX + w, segmentWidth,
                        segment, row, topRemnants, constraints,
                        patterns, seen, ref globalPatternId, maxPatterns, minFirstBlock, minLastBlock);
                    currentBlocks.RemoveAt(currentBlocks.Count - 1);
                }
            }

            // 2. Ветка: Добавить Остаток
            bool canAddRemnant = isFirstBlock || currentBlocks[currentBlocks.Count - 1].Type == BlockType.New;
            if (canAddRemnant)
            {
                foreach (var remnant in topRemnants)
                {
                    if (usedRemnantIds.Contains(remnant.Id)) continue;
                    double rw = GetUsableWidth(remnant, row.Height);
                    if (rw < minWidth) continue;
                    
                    double useW = Math.Min(rw, remaining);
                    if (useW < minWidth) continue;
                    
                    double leftAfter = remaining - useW;
                    if (leftAfter > TOLERANCE && leftAfter < minLastBlock)
                        continue;

                    usedRemnantIds.Add(remnant.Id);
                    currentBlocks.Add(new Block
                    {
                        X = segment.StartX + currentX, Width = useW,
                        Type = useW < rw - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                        RemnantId = remnant.Id, CutFrom = rw,
                        IsRotated = remnant.Height < row.Height && remnant.Width >= row.Height
                    });

                    TryAlternatingRemnantsRecursive(
                        currentBlocks, usedRemnantIds, currentX + useW, segmentWidth,
                        segment, row, topRemnants, constraints,
                        patterns, seen, ref globalPatternId, maxPatterns, minFirstBlock, minLastBlock);

                    currentBlocks.RemoveAt(currentBlocks.Count - 1);
                    usedRemnantIds.Remove(remnant.Id);
                }
            }
        }

        /// <summary>
        /// Эвристика K (Look-ahead): создаёт паттерн с первым блоком заданной ширины,
        /// остальное заполняется целыми новыми плитами. Цель: создать остаток = TileWidth - idealFirstBlock,
        /// который совпадёт с шириной будущего сегмента.
        /// </summary>
        private static void TryLookAheadPattern(
            double idealFirstBlock, double segmentWidth,
            RowSegment segment, RowDefinition row,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen,
            ref int globalPatternId,
            double minFirstBlock, double minLastBlock)
        {
            var blocks = new List<Block>();
            double x = 0;

            // Первый блок: обрезанная новая плита с шириной idealFirstBlock
            blocks.Add(new Block
            {
                X = segment.StartX + x,
                Width = idealFirstBlock,
                Type = BlockType.New
            });
            x += idealFirstBlock;

            // Заполняем оставшееся целыми новыми плитами
            double remaining = segmentWidth - x;
            while (remaining > TOLERANCE)
            {
                double tileW = Math.Min(constraints.TileWidth, remaining);
                double afterTile = remaining - tileW;
                if (afterTile > TOLERANCE && afterTile < minLastBlock)
                {
                    if (remaining <= constraints.TileWidth + TOLERANCE)
                        tileW = remaining;
                }

                if (tileW < minLastBlock && blocks.Count > 0)
                {
                    var last = blocks[blocks.Count - 1];
                    if (last.Type == BlockType.New && last.Width + remaining <= constraints.TileWidth + TOLERANCE)
                    {
                        blocks[blocks.Count - 1] = new Block
                        {
                            X = last.X, Width = last.Width + remaining, Type = BlockType.New
                        };
                        remaining = 0;
                        break;
                    }
                    return; // паттерн невалидный
                }

                blocks.Add(new Block
                {
                    X = segment.StartX + x,
                    Width = tileW,
                    Type = BlockType.New
                });
                x += tileW;
                remaining = segmentWidth - x;
            }

            double total = blocks.Sum(b => b.Width);
            if (Math.Abs(total - segmentWidth) > TOLERANCE) return;

            string key = PatternKey(blocks);
            if (seen.Add(key))
            {
                var usedIds = new HashSet<Guid>();
                var pattern = CreatePattern(blocks, segment, row, usedIds, ref globalPatternId, constraints);
                patterns.Add(pattern);
            }
        }

        /// <summary>Уникальный ключ паттерна для дедупликации между фазами.</summary>
        private static string PatternKey(List<Block> blocks)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var b in blocks)
            {
                sb.Append(b.Width.ToString("F1"));
                sb.Append(b.Type == BlockType.New ? "N" : b.Type == BlockType.Remnant ? "R" : "C");
                if (b.RemnantId.HasValue) sb.Append(b.RemnantId.Value.ToString("N").Substring(0, 8));
                sb.Append('|');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Рекурсивная генерация всех допустимых комбинаций блоков.
        /// Параметр remnantFirst определяет порядок: остатки или новые плиты первыми.
        /// maxBlocks ограничивает число блоков в паттерне для контроля комбинаторики.
        /// </summary>
        private static void GenerateRecursive(
            List<Block> currentBlocks,
            HashSet<Guid> usedRemnantIds,
            double currentX,
            double segmentWidth,
            RowSegment segment,
            RowDefinition row,
            List<Remnant> availableRemnants,
            OptimizationConstraints constraints,
            List<BlockPattern> patterns,
            HashSet<string> seen,
            ref int globalPatternId,
            int maxPatterns,
            double minFirstBlock,
            double minMiddleBlock,
            double minLastBlock,
            int consecutiveRemnants,
            bool remnantFirst,
            int maxBlocks)
        {
            if (patterns.Count >= maxPatterns)
                return;
            
            // Ограничение глубины: не более maxBlocks блоков
            if (currentBlocks.Count >= maxBlocks)
                return;

            double remaining = segmentWidth - currentX;

            // База рекурсии: сегмент заполнен
            if (Math.Abs(remaining) < TOLERANCE)
            {
                string key = PatternKey(currentBlocks);
                if (seen.Add(key))
                {
                    var pattern = CreatePattern(currentBlocks, segment, row, usedRemnantIds, ref globalPatternId, constraints);
                    patterns.Add(pattern);
                }
                return;
            }

            if (remaining < 0)
                return;

            bool isFirstBlock = currentBlocks.Count == 0;
            double minWidth = isFirstBlock ? minFirstBlock : minMiddleBlock;

            // Проверка: хватит ли места для следующего блока
            if (remaining < minWidth)
            {
                if (currentBlocks.Count > 0)
                {
                    var lastBlock = currentBlocks[currentBlocks.Count - 1];
                    double newWidth = lastBlock.Width + remaining;
                    double maxExpansion = lastBlock.Type == BlockType.New
                        ? constraints.TileWidth
                        : Math.Min(lastBlock.CutFrom, constraints.TileWidth);
                    bool isValidExpansion = newWidth <= maxExpansion + TOLERANCE;

                    if (newWidth >= minLastBlock && isValidExpansion)
                    {
                        var expandedBlocks = new List<Block>(currentBlocks);
                        expandedBlocks[expandedBlocks.Count - 1] = new Block
                        {
                            X = lastBlock.X,
                            Width = newWidth,
                            Type = lastBlock.Type,
                            RemnantId = lastBlock.RemnantId,
                            CutFrom = lastBlock.CutFrom,
                            IsRotated = lastBlock.IsRotated
                        };
                        string key = PatternKey(expandedBlocks);
                        if (seen.Add(key))
                        {
                            var pattern = CreatePattern(expandedBlocks, segment, row, usedRemnantIds, ref globalPatternId, constraints);
                            patterns.Add(pattern);
                        }
                    }
                }
                return;
            }

            double maxCurrentWidth = remaining - minLastBlock;
            if (maxCurrentWidth < minWidth && remaining >= minWidth)
            {
                minWidth = remaining;
                maxCurrentWidth = remaining;
            }

            if (remnantFirst)
            {
                // СНАЧАЛА остатки, потом новые плиты
                TryRemnants(currentBlocks, usedRemnantIds, currentX, segmentWidth, segment, row,
                    availableRemnants, constraints, patterns, seen, ref globalPatternId, maxPatterns,
                    minFirstBlock, minMiddleBlock, minLastBlock, consecutiveRemnants, remaining, minWidth, remnantFirst, maxBlocks);
                TryNewTiles(currentBlocks, usedRemnantIds, currentX, segmentWidth, segment, row,
                    availableRemnants, constraints, patterns, seen, ref globalPatternId, maxPatterns,
                    minFirstBlock, minMiddleBlock, minLastBlock, remaining, minWidth, remnantFirst, maxBlocks);
            }
            else
            {
                // СНАЧАЛА новые плиты, потом остатки
                TryNewTiles(currentBlocks, usedRemnantIds, currentX, segmentWidth, segment, row,
                    availableRemnants, constraints, patterns, seen, ref globalPatternId, maxPatterns,
                    minFirstBlock, minMiddleBlock, minLastBlock, remaining, minWidth, remnantFirst, maxBlocks);
                TryRemnants(currentBlocks, usedRemnantIds, currentX, segmentWidth, segment, row,
                    availableRemnants, constraints, patterns, seen, ref globalPatternId, maxPatterns,
                    minFirstBlock, minMiddleBlock, minLastBlock, consecutiveRemnants, remaining, minWidth, remnantFirst, maxBlocks);
            }
        }

        /// <summary>Пробует варианты с новыми плитами</summary>
        private static void TryNewTiles(
            List<Block> currentBlocks, HashSet<Guid> usedRemnantIds,
            double currentX, double segmentWidth, RowSegment segment, RowDefinition row,
            List<Remnant> availableRemnants, OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen, ref int globalPatternId, int maxPatterns,
            double minFirstBlock, double minMiddleBlock, double minLastBlock,
            double remaining, double minWidth, bool remnantFirst, int maxBlocks)
        {
            bool isFirstBlock = currentBlocks.Count == 0;
            foreach (var width in GetBlockWidthOptions(minWidth, Math.Min(remaining, constraints.TileWidth), constraints, isFirstBlock))
            {
                if (patterns.Count >= maxPatterns) break;

                double leftAfter = remaining - width;
                if (leftAfter > TOLERANCE && leftAfter < minLastBlock)
                    continue;

                currentBlocks.Add(new Block
                {
                    X = segment.StartX + currentX,
                    Width = width,
                    Type = BlockType.New,
                    RemnantId = null
                });

                GenerateRecursive(
                    currentBlocks, usedRemnantIds, currentX + width, segmentWidth,
                    segment, row, availableRemnants, constraints, patterns, seen,
                    ref globalPatternId, maxPatterns, minFirstBlock, minMiddleBlock, minLastBlock,
                    0, remnantFirst, maxBlocks);

                currentBlocks.RemoveAt(currentBlocks.Count - 1);
            }
        }

        /// <summary>Пробует варианты с остатками из склада</summary>
        private static void TryRemnants(
            List<Block> currentBlocks, HashSet<Guid> usedRemnantIds,
            double currentX, double segmentWidth, RowSegment segment, RowDefinition row,
            List<Remnant> availableRemnants, OptimizationConstraints constraints,
            List<BlockPattern> patterns, HashSet<string> seen, ref int globalPatternId, int maxPatterns,
            double minFirstBlock, double minMiddleBlock, double minLastBlock,
            int consecutiveRemnants, double remaining, double minWidth, bool remnantFirst, int maxBlocks)
        {
            int maxConsecutive = (segment.NearWindowLeft || segment.NearWindowRight) 
                ? constraints.MaxConsecutiveRemnantsWindow 
                : constraints.MaxConsecutiveRemnants;

            if (consecutiveRemnants >= maxConsecutive)
                return;

            foreach (var remnant in availableRemnants)
            {
                if (patterns.Count >= maxPatterns) break;
                if (usedRemnantIds.Contains(remnant.Id)) continue;

                double remnantWidth = GetUsableWidth(remnant, row.Height);
                if (remnantWidth < minWidth) continue;

                foreach (var width in GetRemnantWidthOptions(remnantWidth, minWidth, Math.Min(remaining, remnantWidth), constraints))
                {
                    if (patterns.Count >= maxPatterns) break;

                    double leftAfter = remaining - width;
                    if (leftAfter > TOLERANCE && leftAfter < minLastBlock)
                        continue;

                    usedRemnantIds.Add(remnant.Id);
                    currentBlocks.Add(new Block
                    {
                        X = segment.StartX + currentX,
                        Width = width,
                        Type = width < remnantWidth - TOLERANCE ? BlockType.CutRemnant : BlockType.Remnant,
                        RemnantId = remnant.Id,
                        CutFrom = remnantWidth,
                        IsRotated = remnant.Height < row.Height && remnant.Width >= row.Height
                    });

                    GenerateRecursive(
                        currentBlocks, usedRemnantIds, currentX + width, segmentWidth,
                        segment, row, availableRemnants, constraints, patterns, seen,
                        ref globalPatternId, maxPatterns, minFirstBlock, minMiddleBlock, minLastBlock,
                        consecutiveRemnants + 1, remnantFirst, maxBlocks);

                    currentBlocks.RemoveAt(currentBlocks.Count - 1);
                    usedRemnantIds.Remove(remnant.Id);
                }
            }
        }

        /// <summary>
        /// Создаёт объект BlockPattern из списка блоков
        /// </summary>
        private static BlockPattern CreatePattern(
            List<Block> blocks,
            RowSegment segment,
            RowDefinition row,
            HashSet<Guid> usedRemnantIds,
            ref int globalPatternId,
            OptimizationConstraints constraints)
        {
            var pattern = new BlockPattern
            {
                PatternId = globalPatternId++,
                RowIndex = segment.RowIndex,
                SegmentIndex = segment.SegmentIndex,
                Blocks = new List<Block>(blocks),
                UsedRemnantIds = new HashSet<Guid>(usedRemnantIds)
            };

            // Вычисляем позиции стыков (для ограничения перевязки)
            double x = segment.StartX;
            foreach (var block in blocks)
            {
                x += block.Width;
                if (Math.Abs(x - segment.EndX) > TOLERANCE) // Не добавляем конец сегмента
                {
                    pattern.JointPositions.Add(x);
                }
            }

            // Считаем площадь использованных и созданных остатков, а также кол-во новых плит
            foreach (var block in blocks)
            {
                if (block.Type == BlockType.Remnant || block.Type == BlockType.CutRemnant)
                {
                    pattern.UsedRemnantArea += block.Width * row.Height;
                    
                    // Если остаток раскроен — создаётся новый остаток
                    if (block.Type == BlockType.CutRemnant)
                    {
                        double leftover = block.CutFrom - block.Width;
                        if (leftover >= constraints.MinRemnantToSave)
                        {
                            // FIX 4.2: penalty=4 для мелких отходов (< MinBlock), которые не сохраняются — чистые потери
                        double penalty = leftover < constraints.MinBlock ? 4.0 : 1.0;
                            pattern.CreatedRemnantArea += leftover * row.Height * penalty;
                        }
                    }
                }
                else if (block.Type == BlockType.New)
                {
                    pattern.NewTileCount++;
                    double leftover = constraints.TileWidth - block.Width;
                    if (leftover >= constraints.MinRemnantToSave && leftover > TOLERANCE)
                    {
                        // FIX 4.2: penalty=4 для мелких отходов (< MinBlock), которые не сохраняются — чистые потери
                        double penalty = leftover < constraints.MinBlock ? 4.0 : 1.0;
                        pattern.CreatedRemnantArea += leftover * row.Height * penalty;
                    }
                    
                    // BUG-7 fix: track height waste in CreatedRemnantArea so solver can penalize it
                    if (row.Height < constraints.TileHeight - TOLERANCE)
                    {
                        double heightLeftover = constraints.TileHeight - row.Height;
                        if (heightLeftover > TOLERANCE && heightLeftover < constraints.MinRemnantToSave)
                        {
                            // Sub-threshold height waste — add to CreatedRemnantArea as penalty
                            pattern.CreatedRemnantArea += heightLeftover * constraints.TileWidth;
                        }
                    }
                }
            }

            return pattern;
        }

        /// <summary>
        /// Проверяет, можно ли использовать остаток для ряда данной высоты
        /// </summary>
        public static bool CanUseRemnant(Remnant remnant, double rowHeight, OptimizationConstraints constraints)
        {
            // Можно использовать если одна из сторон >= высоты ряда
            // и другая сторона >= минимальной допустимой ширины (разрешаем мелкие обрезки от окон)
            double minAllowed = Math.Min(constraints.MinBlock, constraints.MinBlockNearWindow);
            return (remnant.Height >= rowHeight && remnant.Width >= minAllowed) ||
                   (remnant.Width >= rowHeight && remnant.Height >= minAllowed);
        }

        /// <summary>
        /// Возвращает ширину остатка при использовании в ряду данной высоты
        /// </summary>
        private static double GetUsableWidth(Remnant remnant, double rowHeight)
        {
            if (remnant.Height >= rowHeight)
                return remnant.Width;
            if (remnant.Width >= rowHeight)
                return remnant.Height;
            return 0;
        }

        /// <summary>
        /// Генерирует варианты ширин для новых плит.
        /// Логика каменщика: кладём ЦЕЛУЮ плитку. Разные ширины —
        /// ТОЛЬКО для первого блока (создаёт разные позиции стыков для перевязки).
        /// Для средних/последних блоков: только полная плита или точный зазор.
        /// </summary>
        private static IEnumerable<double> GetBlockWidthOptions(
            double minWidth, double maxWidth, OptimizationConstraints constraints, bool isFirstBlock)
        {
            // 1. Полная плита — ВСЕГДА в приоритете (0 отходов)
            if (constraints.TileWidth >= minWidth && constraints.TileWidth <= maxWidth + TOLERANCE)
                yield return constraints.TileWidth;

            // 2. Точный оставшийся зазор (если меньше полной плиты)
            if (maxWidth < constraints.TileWidth - TOLERANCE && maxWidth >= minWidth)
                yield return maxWidth;

            // 3. Для ПЕРВОГО блока: разные ширины с шагом MinJointOffset — для перевязки швов
            if (isFirstBlock)
            {
                double step = Math.Max(50, constraints.MinJointOffset);
                for (double w = Math.Ceiling(minWidth / step) * step; w <= maxWidth; w += step)
                {
                    if (Math.Abs(w - constraints.TileWidth) > TOLERANCE &&
                        Math.Abs(w - maxWidth) > TOLERANCE)
                        yield return w;
                }
                // Точный минимальный размер (для узких зазоров)
                if (minWidth >= constraints.MinBlock &&
                    Math.Abs(minWidth - constraints.TileWidth) > TOLERANCE &&
                    Math.Abs(minWidth - maxWidth) > TOLERANCE &&
                    minWidth % step > TOLERANCE)
                {
                    yield return minWidth;
                }
            }
        }

        /// <summary>
        /// Генерирует варианты ширин для использования остатка
        /// </summary>
        private static IEnumerable<double> GetRemnantWidthOptions(
            double remnantWidth, double minWidth, double maxWidth, OptimizationConstraints constraints)
        {
            // Шаг = MinJointOffset (100 мм) — минимальное значимое смещение стыка
            double step = Math.Max(50, constraints.MinJointOffset);
            
            // 1. Полный остаток — предпочтительно (минимум отходов)
            if (remnantWidth >= minWidth && remnantWidth <= maxWidth + TOLERANCE)
                yield return remnantWidth;

            // 2. Раскрой с шагом MinJointOffset
            for (double w = Math.Ceiling(minWidth / step) * step; w < remnantWidth && w <= maxWidth; w += step)
            {
                yield return w;
            }
            
            // 3. Точный размер для заполнения остатка сегмента
            if (maxWidth < remnantWidth - TOLERANCE && maxWidth >= minWidth && maxWidth % step > TOLERANCE)
            {
                yield return maxWidth;
            }
            
            // 4. Точный минимальный размер (для узких зазоров)
            if (minWidth >= constraints.MinBlock && minWidth % step != 0 && minWidth < remnantWidth)
            {
                yield return minWidth;
            }
        }

        /// <summary>
        /// Фильтрует паттерны по дополнительным критериям (например, минимизация количества блоков)
        /// </summary>
        public static List<BlockPattern> FilterPatterns(
            List<BlockPattern> patterns, int maxCount)
        {
            if (patterns.Count <= maxCount)
                return patterns;

            // Сортируем по потенциальной эффективности
            return patterns
                .OrderByDescending(p => p.UsedRemnantArea) // Приоритет: больше использованных остатков
                .ThenBy(p => p.CreatedRemnantArea)          // Меньше созданных остатков
                .ThenBy(p => p.Blocks.Count)                // Меньше блоков
                .Take(maxCount)
                .ToList();
        }
    }
}
