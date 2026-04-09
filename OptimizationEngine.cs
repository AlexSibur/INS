using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;
using InsulationMasterPro.Geometry;
using InsulationMasterPro.Data;

namespace InsulationMasterPro.Logic
{
    public class OptimizationResult
    {
        public double Offset { get; set; }
        public double TotalWasteArea { get; set; }
        public int TilesUsed { get; set; }
        public double UtilizationRatio { get; set; }
        public List<Polyline> LayoutTiles { get; set; } = new List<Polyline>();
        /// <summary>Ряд с выпуском за контур (первая плита выходит на 170 мм).</summary>
        public bool RowHasRelease { get; set; }
        /// <summary>Области для штриховки (перекрытия блоков с окнами в верхнем/нижнем рядах, ТЗ п.2.7).</summary>
        public List<Polyline> HatchRegions { get; set; } = new List<Polyline>();
        /// <summary>X-координаты стыков (швов) между плитками в ряду — для проверки перевязки с соседними рядами.</summary>
        public List<double> JointPositions { get; set; } = new List<double>();
        /// <summary>Фактическая высота ряда (600 мм стандарт, меньше для верхнего ряда / ряда из коротких остатков).</summary>
        public double RowHeight { get; set; } = 600.0;
    }

    public static class OptimizationEngine
    {
        private const double TILE_WIDTH = 1200.0;
        private const double TILE_HEIGHT = 600.0;
        private const double DEFAULT_MIN_JOINT_OFFSET = 100.0;
        private const double DEFAULT_MAX_JOINT_OFFSET = 400.0;
        private const double OPTIMIZATION_STEP = 10.0; // Шаг перебора перекрытия (мм) — ТЗ п.3.3
        private const int MAX_ITERATIONS = 20;
        private const int LOOK_AHEAD_ROWS = 10; // Количество рядов для look-ahead оптимизации — ТЗ п.3.3

        private const double RELEASE_OVERHANG = 170.0; // Выпуск за контур (мм) — ТЗ п.2.2
        private const double MIN_RELEASE_PIECE = 370.0; // Минимальная длина куска выпуска (150+20+200) — ТЗ п.2.2
        private const double MIN_REMNANT_WIDTH_ROW = 200.0; // Минимальная ширина остатка в ряду — ТЗ п.2.3

        private static double NormalizeOffset(double offset)
        {
            double normalized = offset % TILE_WIDTH;
            return normalized < 0 ? normalized + TILE_WIDTH : normalized;
        }

        private static double CircularDistance(double a, double b)
        {
            double diff = Math.Abs(a - b);
            return diff > TILE_WIDTH / 2 ? TILE_WIDTH - diff : diff;
        }

        private static bool IsOffsetValid(double previousOffset, double candidateOffset, double minOverlap)
        {
            if (minOverlap <= 0) return true;
            double prev = NormalizeOffset(previousOffset);
            double candidate = NormalizeOffset(candidateOffset);
            return CircularDistance(prev, candidate) >= minOverlap;
        }

        /// <summary>
        /// Минимальное расстояние между стыками текущего и предыдущего ряда.
        /// Возвращает double.MaxValue если один из списков пуст.
        /// </summary>
        private static double GetMinJointStagger(List<double> currentJoints, List<double> previousJoints)
        {
            if (currentJoints == null || !currentJoints.Any() || previousJoints == null || !previousJoints.Any())
                return double.MaxValue;
            double minDist = double.MaxValue;
            foreach (var j1 in currentJoints)
                foreach (var j2 in previousJoints)
                    minDist = Math.Min(minDist, Math.Abs(j1 - j2));
            return minDist;
        }

        /// <summary>
        /// Быстрая оценка суммарных отходов для N будущих рядов (без геометрических операций Clipper2).
        /// Используется для look-ahead: оценивает, какой набор оставшихся остатков лучше сохранить.
        /// </summary>
        private static double QuickEstimateFutureWaste(double facadeWidth, List<Remnant> remainingRemnants,
            int numRows, bool firstRowWithRelease, int startRowIndex)
        {
            double totalWaste = 0;
            var simRemnants = new List<Remnant>(remainingRemnants);

            for (int i = 0; i < numRows; i++)
            {
                int rowIdx = startRowIndex + i;
                bool hasRelease = firstRowWithRelease ? (rowIdx % 2 == 0) : (rowIdx % 2 == 1);
                double startX = hasRelease ? -RELEASE_OVERHANG : 0;
                double endX = hasRelease ? facadeWidth + RELEASE_OVERHANG : facadeWidth;
                double x = startX;
                int consecutiveRemnants = 0;
                bool isFirstTile = true;

                while (x < endX)
                {
                    double space = endX - x;
                    if (space < MIN_REMNANT_WIDTH_ROW) break; // мин. блок 200 мм

                    double tileW = Math.Min(TILE_WIDTH, space);
                    bool usedRemnant = false;

                    if (consecutiveRemnants < 2 && space >= MIN_REMNANT_WIDTH_ROW)
                    {
                        double minW = (isFirstTile && hasRelease) ? MIN_RELEASE_PIECE : MIN_REMNANT_WIDTH_ROW;
                        var match = RemnantManager.FindBestRemnant(simRemnants, tileW, TILE_HEIGHT, false, minWidth: minW);
                        if (match != null)
                        {
                            double rw = match.RequiresRotation ? match.Remnant.Height : match.Remnant.Width;
                            tileW = Math.Min(rw, space);
                            simRemnants.Remove(match.Remnant);
                            usedRemnant = true;
                            consecutiveRemnants++;
                        }
                    }

                    // Проверка: не оставлять блок < 200 мм после текущего
                    double spaceAfter = space - tileW;
                    if (spaceAfter > 0 && spaceAfter < MIN_REMNANT_WIDTH_ROW)
                    {
                        double reduced = space - MIN_REMNANT_WIDTH_ROW;
                        tileW = reduced >= MIN_REMNANT_WIDTH_ROW ? reduced : space;
                    }

                    if (!usedRemnant)
                    {
                        if (tileW < TILE_WIDTH)
                            totalWaste += (TILE_WIDTH - tileW) * TILE_HEIGHT;
                        consecutiveRemnants = 0;
                    }

                    x += tileW;
                    isFirstTile = false;
                }
            }

            return totalWaste;
        }

        /// <summary>
        /// Находит оптимальное смещение для ряда плит.
        /// Проверяет перевязку швов >= 100 мм по ФАКТИЧЕСКИМ позициям стыков (а не только по сдвигу паттерна).
        /// Использует look-ahead на LOOK_AHEAD_ROWS рядов вперёд для оптимального распределения остатков.
        /// </summary>
        public static OptimizationResult FindOptimalRowOffset(Polyline facade, List<Polyline> windows, double rowY,
                                                           double previousRowOffset, List<Remnant> availableRemnants,
                                                           double minOverlap = DEFAULT_MIN_JOINT_OFFSET, double maxOverlap = DEFAULT_MAX_JOINT_OFFSET,
                                                           bool rowHasRelease = false,
                                                           List<double> previousJoints = null,
                                                           bool firstRowWithRelease = true,
                                                           int currentRowIndex = 0,
                                                           double rowHeight = TILE_HEIGHT)
        {
            var offsets = GenerateOffsetOptions(previousRowOffset, minOverlap, maxOverlap, rowHasRelease);
            var allResults = new List<(OptimizationResult result, List<Remnant> remaining, bool staggerOk)>();

            foreach (var offset in offsets)
            {
                var remnantsCopy = new List<Remnant>(availableRemnants);
                var result = EvaluateOffset(facade, windows, rowY, offset, remnantsCopy, rowHasRelease, previousJoints, rowHeight);
                if (result == null) continue;

                result.RowHasRelease = rowHasRelease;

                // Проверка перевязки по ФАКТИЧЕСКИМ стыкам (не только по сдвигу паттерна)
                bool staggerOk = true;
                if (previousJoints != null && previousJoints.Any() && result.JointPositions.Any())
                {
                    double minStagger = GetMinJointStagger(result.JointPositions, previousJoints);
                    staggerOk = minStagger >= DEFAULT_MIN_JOINT_OFFSET;
                }

                allResults.Add((result, remnantsCopy, staggerOk));
            }

            if (!allResults.Any())
            {
                double fallbackOffset;
                if (rowHasRelease)
                {
                    var extents = facade.GeometricExtents;
                    double minX = extents.MinPoint.X;
                    fallbackOffset = NormalizeOffset(minX - RELEASE_OVERHANG);
                }
                else
                {
                    fallbackOffset = NormalizeOffset(previousRowOffset + minOverlap);
                }
                return new OptimizationResult
                {
                    Offset = fallbackOffset,
                    TotalWasteArea = double.MaxValue,
                    TilesUsed = 0,
                    UtilizationRatio = 0,
                    RowHasRelease = rowHasRelease
                };
            }

            // Предпочитаем результаты с правильной перевязкой
            var candidates = allResults.Where(r => r.staggerOk).ToList();
            if (!candidates.Any())
            {
                // Все нарушают — берём кандидатов с наибольшим минимальным зазором между стыками
                candidates = allResults
                    .OrderByDescending(r => GetMinJointStagger(r.result.JointPositions, previousJoints))
                    .Take(5)
                    .ToList();
            }

            // Look-ahead: оцениваем будущие ряды для каждого кандидата
            var facadeExtents2 = facade.GeometricExtents;
            double facadeWidth = facadeExtents2.MaxPoint.X - facadeExtents2.MinPoint.X;
            double facadeMaxY = facadeExtents2.MaxPoint.Y;
            int remainingRows = Math.Max(0, (int)((facadeMaxY - rowY - rowHeight) / TILE_HEIGHT));
            int lookAheadRows = Math.Min(LOOK_AHEAD_ROWS, remainingRows);

            // ТЗ v2.5: Главная цель — МИНИМАЛЬНАЯ СУММАРНАЯ ПЛОЩАДЬ ОСТАТКОВ НА СКЛАДЕ.
            // Выбирается вариант, при котором после раскладки ряда на складе остаётся
            // наименьшая суммарная площадь остатков. Без штрафов и весовых коэффициентов.
            var scoredCandidates = new List<(OptimizationResult result, double totalScore)>();
            foreach (var c in candidates)
            {
                // Суммарная площадь остатков на складе после раскладки этого ряда
                double remainingArea = c.remaining.Sum(r => r.Area);

                // Look-ahead: оценка будущих отходов (чем меньше — тем лучше)
                double lookAheadWaste = 0;
                if (lookAheadRows > 0)
                {
                    lookAheadWaste = QuickEstimateFutureWaste(
                        facadeWidth, c.remaining, lookAheadRows,
                        firstRowWithRelease, currentRowIndex + 1);
                }

                // Основной критерий: минимальная суммарная площадь остатков на складе
                // + look-ahead отходы — чтобы учитывать перспективу использования
                double score = remainingArea + lookAheadWaste;

                scoredCandidates.Add((c.result, score));
            }

            var best = scoredCandidates
                .OrderBy(c => c.totalScore)
                .ThenByDescending(c => c.result.UtilizationRatio)
                .First();

            if (rowHasRelease)
            {
                var extents = facade.GeometricExtents;
                double minX = extents.MinPoint.X;
                double bondOffset = (minX - RELEASE_OVERHANG) % TILE_WIDTH;
                if (bondOffset < 0) bondOffset += TILE_WIDTH;
                best.result.Offset = bondOffset;
            }
            return best.result;
        }

        /// <summary>
        /// Генерирует варианты смещений. Шаг перебора — 10 мм (ТЗ п.3.3).
        /// Для ряда с выпуском первая плита начинается с minX - 170, смещение для перевязки учитывается от этой точки.
        /// </summary>
        private static List<double> GenerateOffsetOptions(double previousOffset, double minOverlap, double maxOverlap, bool rowHasRelease)
        {
            var offsets = new List<double>();
            double step = OPTIMIZATION_STEP; // 10 мм — ТЗ п.3.3

            for (double overlap = minOverlap; overlap <= maxOverlap; overlap += step)
            {
                double offset = (previousOffset + overlap) % TILE_WIDTH;
                offsets.Add(NormalizeOffset(offset));
            }
            offsets.Add(NormalizeOffset(previousOffset + maxOverlap));

            if (!rowHasRelease)
            {
                offsets.Add(0);
                offsets.Add(TILE_WIDTH / 4);
                offsets.Add(TILE_WIDTH / 2);
                offsets.Add(TILE_WIDTH * 3 / 4);
            }
            else
            {
                // Для ряда с выпуском: смещение от (minX - 170), т.е. эффективное смещение для следующего ряда
                double releaseStart = -RELEASE_OVERHANG;
                double effOffset = (releaseStart + previousOffset + minOverlap) % TILE_WIDTH;
                offsets.Add(NormalizeOffset(effOffset));
            }
            var filteredOffsets = offsets
                .Where(o => IsOffsetValid(previousOffset, o, minOverlap))
                .Distinct()
                .ToList();
            return filteredOffsets;
        }

        /// <summary>
        /// Определяет окна, для которых текущий ряд является верхним или нижним (ТЗ п.2.7).
        /// Блоки в таких рядах НЕ обрезаются по контуру этих окон, а перекрываемые части штрихуются.
        /// </summary>
        private static List<Polyline> GetExcludedWindowsForRow(List<Polyline> windows, double rowY, double rowHeight)
        {
            if (windows == null || !windows.Any()) return new List<Polyline>();

            return windows.Where(w =>
            {
                double wMinY = w.GeometricExtents.MinPoint.Y;
                double wMaxY = w.GeometricExtents.MaxPoint.Y;
                // Нижний ряд окна: ряд начинается ниже нижнего края окна и заходит выше
                bool isBottom = rowY < wMinY && rowY + rowHeight > wMinY;
                // Верхний ряд окна: ряд начинается ниже верхнего края окна и заходит выше
                bool isTop = rowY < wMaxY && rowY + rowHeight > wMaxY;
                return isBottom || isTop;
            }).ToList();
        }

        /// <summary>
        /// Оценивает конкретное смещение. rowHasRelease: выпуск слева (первая плита от minX-170) и справа (последняя до maxX+170),
        /// обрезка по расширенному фасаду и окнам. ТЗ v2.2: не более 2 остатков подряд.
        /// </summary>
        /// <summary>
        /// Минимальное расстояние от точки x до ближайшего стыка из списка.
        /// </summary>
        private static double MinDistToJoints(double x, List<double> joints)
        {
            double min = double.MaxValue;
            foreach (var j in joints) min = Math.Min(min, Math.Abs(x - j));
            return min;
        }

        private static OptimizationResult EvaluateOffset(Polyline facade, List<Polyline> windows,
                                                      double rowY, double offset, List<Remnant> availableRemnants,
                                                      bool rowHasRelease, List<double> previousJoints = null,
                                                      double rowHeight = TILE_HEIGHT)
        {
            var result = new OptimizationResult { Offset = offset };
            var layoutTiles = new List<Polyline>();
            
            var extents = facade.GeometricExtents;
            double minX = extents.MinPoint.X;
            double maxX = extents.MaxPoint.X;
            double targetRight = rowHasRelease ? maxX + RELEASE_OVERHANG : maxX;
            Polyline effectiveFacade = rowHasRelease ? GeometryUtils.ExtendFacadeLeftRight(facade, RELEASE_OVERHANG) : facade;

            // ТЗ п.2.7: определяем окна, для которых этот ряд является верхним/нижним
            var excludedWindows = GetExcludedWindowsForRow(windows, rowY, rowHeight);

            const double MIN_REMNANT_LENGTH_ROW = 200.0; // остаток в ряду не короче 200 мм
            const int MAX_CONSECUTIVE_REMNANTS = 2; // ТЗ: не более 2 остатков подряд

            double currentX;
            bool firstIsReused = false;

            if (rowHasRelease)
            {
                // Первая плита с выпуском: минимальная длина куска — 370 мм (ТЗ п.2.2)
                currentX = minX - RELEASE_OVERHANG;
                double spaceFirst = targetRight - currentX;
                double firstSlotWidth = Math.Min(TILE_WIDTH, spaceFirst);
                double firstTileWidth = firstSlotWidth;
                if (firstSlotWidth >= MIN_RELEASE_PIECE) // 370 мм — ТЗ п.2.2 (170+200)
                {
                    var firstRemnant = FindSuitableRemnant(availableRemnants, currentX, rowY, firstSlotWidth, MIN_RELEASE_PIECE, slotHeight: rowHeight);
                    if (firstRemnant != null)
                    {
                        double fw = firstRemnant.RequiresRotation ? firstRemnant.Remnant.Height : firstRemnant.Remnant.Width;
                        double fh = firstRemnant.RequiresRotation ? firstRemnant.Remnant.Width : firstRemnant.Remnant.Height;
                        firstTileWidth = Math.Min(fw, firstSlotWidth);
                        firstIsReused = true;
                        availableRemnants.Remove(firstRemnant.Remnant);

                        // Если остаток больше — возвращаем неиспользованную часть в склад (кроим остаток)
                        if (fw > firstTileWidth || fh > rowHeight)
                        {
                            var leftovers = RemnantManager.CreateRemnantsFromCut(fw, fh, firstTileWidth, rowHeight, false);
                            foreach (var r in leftovers)
                                availableRemnants.Add(r);
                        }
                    }
                }

                // Проверка: не оставлять блок < 200 мм после первого блока выпуска
                double spaceAfterFirst = spaceFirst - firstTileWidth;
                if (spaceAfterFirst > 0 && spaceAfterFirst < MIN_REMNANT_LENGTH_ROW)
                {
                    double reduced = spaceFirst - MIN_REMNANT_LENGTH_ROW;
                    firstTileWidth = reduced >= MIN_REMNANT_LENGTH_ROW ? reduced : spaceFirst;
                }

                Polyline firstTile = GeometryUtils.CreateRect(currentX, rowY, firstTileWidth, rowHeight);
                var (cutFirst, hatchFirst) = CutTileToBoundaries(firstTile, effectiveFacade, windows, excludedWindows);
                result.HatchRegions.AddRange(hatchFirst);
                if (cutFirst.Any())
                {
                    // Строгое правило: в ряду не допускаются вставки < 200 мм
                    if (cutFirst.Any(t =>
                    {
                        var e = t.GeometricExtents;
                        return (e.MaxPoint.X - e.MinPoint.X) < MIN_REMNANT_LENGTH_ROW;
                    }))
                    {
                        return null; // этот вариант раскладки недопустим
                    }

                    layoutTiles.AddRange(cutFirst);
                    if (!firstIsReused) result.TilesUsed++;
                }
                currentX = minX - RELEASE_OVERHANG + firstTileWidth;
            }
            else
            {
                currentX = minX - offset;
            }
            
            // Блок выпуска всегда короткий (< 1200 мм) → он ВСЕГДА считается как остаток
            // для счётчика подряд, независимо от того, взят ли он из БД остатков или вырезан из новой плиты
            int consecutiveRemnants = rowHasRelease ? 1 : 0;

            // Отслеживание фактических позиций стыков между плитками (для проверки перевязки)
            var jointPositions = new List<double>();
            bool hadPreviousTile = rowHasRelease; // выпускная плита уже размещена перед циклом

            while (currentX < targetRight)
            {
                double spaceLeft = targetRight - currentX;
                // ТЗ: минимальный блок 200 мм. Если остаток пространства < 200, он должен был быть
                // поглощён предыдущей плиткой (см. проверку spaceAfterTile ниже).
                if (spaceLeft < MIN_REMNANT_LENGTH_ROW) break;

                // Фиксируем стык между предыдущей и текущей плиткой
                if (hadPreviousTile)
                    jointPositions.Add(currentX);

                double maxTileWidth = Math.Min(TILE_WIDTH, spaceLeft);
                RemnantMatch remnantMatch = null;
                // ТЗ v2.2: не более 2 остатков подряд; слот не короче 200 мм
                if (spaceLeft >= MIN_REMNANT_LENGTH_ROW && consecutiveRemnants < MAX_CONSECUTIVE_REMNANTS)
                    remnantMatch = FindSuitableRemnant(availableRemnants, currentX, rowY, maxTileWidth, slotHeight: rowHeight);

                // Per-tile проверка перевязки: если остаток создаёт стык ближе 100 мм к стыку предыдущего ряда,
                // а целая плита — нет, отклоняем остаток. Если ОБА нарушают, выбираем вариант с лучшим минимальным расстоянием.
                if (remnantMatch != null && previousJoints != null && previousJoints.Any())
                {
                    double remnantW = remnantMatch.RequiresRotation
                        ? remnantMatch.Remnant.Height : remnantMatch.Remnant.Width;
                    remnantW = Math.Min(remnantW, spaceLeft);
                    double nextJointRem = currentX + remnantW;
                    double nextJointFull = currentX + maxTileWidth;
                    bool isLastTileRem = nextJointRem >= targetRight - 50;
                    bool isLastTileFull = nextJointFull >= targetRight - 50;

                    // Проверяем только если остаток — не последняя плитка (правый край не является стыком)
                    if (!isLastTileRem)
                    {
                        double staggerRem = MinDistToJoints(nextJointRem, previousJoints);
                        double staggerFull = isLastTileFull
                            ? double.MaxValue
                            : MinDistToJoints(nextJointFull, previousJoints);

                        if (staggerRem < DEFAULT_MIN_JOINT_OFFSET
                            && staggerFull >= DEFAULT_MIN_JOINT_OFFSET)
                        {
                            // Остаток нарушает перевязку, целая плита — нет: отклоняем остаток
                            remnantMatch = null;
                        }
                        else if (staggerRem < DEFAULT_MIN_JOINT_OFFSET
                                 && staggerFull < DEFAULT_MIN_JOINT_OFFSET)
                        {
                            // ОБА нарушают — выбираем вариант с лучшим (большим) минимальным расстоянием
                            if (staggerFull > staggerRem)
                            {
                                // Целая плита имеет лучшее расстояние — отклоняем остаток
                                remnantMatch = null;
                            }
                        }
                    }
                }

                Polyline tile;
                bool isReused = false;
                double tileWidth, tileHeight;
                
                if (remnantMatch != null)
                {
                    tileWidth = remnantMatch.RequiresRotation ? remnantMatch.Remnant.Height : remnantMatch.Remnant.Width;
                    tileHeight = remnantMatch.RequiresRotation ? remnantMatch.Remnant.Width : remnantMatch.Remnant.Height;
                    tileWidth = Math.Min(tileWidth, spaceLeft);
                    tileHeight = rowHeight;
                }
                else
                {
                    tileWidth = maxTileWidth;
                    tileHeight = rowHeight;
                }

                // ═══ КРИТИЧЕСКАЯ ПРОВЕРКА: запрет блоков < 200 мм ═══
                // Если после размещения текущей плитки остаётся щель 0 < gap < 200 мм,
                // корректируем ширину: либо подрезаем плитку, чтобы оставить >= 200 мм для следующей,
                // либо расширяем до конца ряда если суммарное пространство < 400 мм.
                double spaceAfterTile = spaceLeft - tileWidth;
                if (spaceAfterTile > 0 && spaceAfterTile < MIN_REMNANT_LENGTH_ROW)
                {
                    double reducedWidth = spaceLeft - MIN_REMNANT_LENGTH_ROW;
                    if (reducedWidth >= MIN_REMNANT_LENGTH_ROW)
                    {
                        // Подрезаем текущую плитку, чтобы следующая была >= 200 мм
                        tileWidth = reducedWidth;
                    }
                    else
                    {
                        // Суммарное пространство < 400 мм — заполняем одной плиткой целиком
                        tileWidth = spaceLeft;
                    }
                }

                tile = GeometryUtils.CreateRect(currentX, rowY, tileWidth, tileHeight);

                if (remnantMatch != null)
                {
                    // Кроим остаток: если он больше используемой плитки — возвращаем остаток в склад
                    double remW = remnantMatch.RequiresRotation ? remnantMatch.Remnant.Height : remnantMatch.Remnant.Width;
                    double remH = remnantMatch.RequiresRotation ? remnantMatch.Remnant.Width : remnantMatch.Remnant.Height;
                    if (remW > tileWidth || remH > tileHeight)
                    {
                        var leftovers = RemnantManager.CreateRemnantsFromCut(remW, remH, tileWidth, tileHeight, false);
                        foreach (var r in leftovers)
                            availableRemnants.Add(r);
                    }

                    isReused = true;
                    availableRemnants.Remove(remnantMatch.Remnant);
                    consecutiveRemnants++;
                }
                else
                {
                    consecutiveRemnants = 0;
                }
                
                var (cutTiles, hatchTiles) = CutTileToBoundaries(tile, effectiveFacade, windows, excludedWindows);
                result.HatchRegions.AddRange(hatchTiles);
                
                if (cutTiles.Any())
                {
                    // Строгое правило: в ряду не допускаются вставки < 200 мм
                    if (cutTiles.Any(t =>
                    {
                        var e = t.GeometricExtents;
                        return (e.MaxPoint.X - e.MinPoint.X) < MIN_REMNANT_LENGTH_ROW;
                    }))
                    {
                        return null; // этот вариант раскладки недопустим
                    }

                    layoutTiles.AddRange(cutTiles);
                    double originalArea = tileWidth * tileHeight;
                    double usedArea = cutTiles.Sum(t => t.Area);
                    double wasteArea = originalArea - usedArea;
                    result.TotalWasteArea += wasteArea;
                    if (!isReused)
                        result.TilesUsed++;
                }
                
                currentX += tileWidth;
                hadPreviousTile = true;
            }
            
            result.JointPositions = jointPositions;
            result.LayoutTiles = layoutTiles;
            double totalUsedArea = layoutTiles.Sum(t => t.Area);
            double totalOriginalArea = result.TilesUsed * TILE_WIDTH * rowHeight;
            result.UtilizationRatio = totalOriginalArea > 0 ? totalUsedArea / totalOriginalArea : 0;
            result.RowHeight = rowHeight;
            
            return result.UtilizationRatio > 0.05 ? result : null;
        }

        /// <summary>
        /// Обрезает плитку по границам фасада и окон.
        /// ТЗ п.2.7: окна из excludedWindows не вычитаются (блоки не обрезаются), вместо этого
        /// пересечения с ними возвращаются как области штриховки (hatchRegions).
        /// </summary>
        private static (List<Polyline> tiles, List<Polyline> hatchRegions) CutTileToBoundaries(
            Polyline tile, Polyline facade, List<Polyline> windows, List<Polyline> excludedWindows)
        {
            // Сначала пересекаем с фасадом
            var intersectionResult = GeometryUtils.BooleanIntersection(tile, new List<Polyline> { facade });
            
            if (!intersectionResult.Any())
            {
                return (new List<Polyline>(), new List<Polyline>());
            }
            
            // Активные окна = все окна минус исключённые (верхний/нижний ряд окна)
            var activeWindows = (excludedWindows != null && excludedWindows.Any())
                ? windows.Where(w => !excludedWindows.Contains(w)).ToList()
                : windows;

            var finalResult = new List<Polyline>();
            var hatchRegions = new List<Polyline>();

            foreach (var piece in intersectionResult)
            {
                // Вычитаем только активные окна
                var cutPieces = GeometryUtils.BooleanSubtract(piece, activeWindows);
                finalResult.AddRange(cutPieces);

                // ТЗ п.2.7: вычисляем области штриховки — пересечения блока с исключёнными окнами
                if (excludedWindows != null)
                {
                    foreach (var excWin in excludedWindows)
                    {
                        var overlap = GeometryUtils.BooleanIntersection(piece, new List<Polyline> { excWin });
                        hatchRegions.AddRange(overlap.Where(o => o.Area > 100));
                    }
                }
            }
            
            // Фильтруем слишком маленькие куски
            return (finalResult.Where(p => p.Area > 10000).ToList(), hatchRegions);
        }

        /// <summary>
        /// Находит подходящий остаток для указанной позиции.
        /// minWidth — минимальная допустимая ширина (200 мм обычно, 370 мм для выпуска).
        /// Остаток НЕ обязан покрывать весь слот 1200 — допустим любой от minWidth.
        /// </summary>
        private static RemnantMatch FindSuitableRemnant(List<Remnant> remnants, double x, double y,
            double maxWidth, double minWidth = MIN_REMNANT_WIDTH_ROW, double slotHeight = TILE_HEIGHT)
        {
            return RemnantManager.FindBestRemnant(remnants, Math.Min(TILE_WIDTH, maxWidth), slotHeight,
                nearWindow: false, minWidth: minWidth);
        }

        /// <summary>
        /// Проверяет, создана ли плитка из остатка (упрощенная версия)
        /// </summary>
        private static bool IsTileFromRemnant(Polyline tile, Remnant remnant)
        {
            var extents = tile.GeometricExtents;
            double width = extents.MaxPoint.X - extents.MinPoint.X;
            double height = extents.MaxPoint.Y - extents.MinPoint.Y;
            
            return (Math.Abs(width - remnant.Width) < 1 && Math.Abs(height - remnant.Height) < 1) ||
                   (Math.Abs(width - remnant.Height) < 1 && Math.Abs(height - remnant.Width) < 1);
        }

        /// <summary>
        /// Оптимизирует раскладку всего фасада. Перекрытие стыков в диапазоне [minOverlap, maxOverlap] мм.
        /// </summary>
        public static List<OptimizationResult> OptimizeFacadeLayout(Polyline facade, List<Polyline> windows,
                                                                   List<Remnant> availableRemnants,
                                                                   double minOverlap = DEFAULT_MIN_JOINT_OFFSET, double maxOverlap = DEFAULT_MAX_JOINT_OFFSET)
        {
            var results = new List<OptimizationResult>();
            var extents = facade.GeometricExtents;
            double minY = extents.MinPoint.Y;
            double maxY = extents.MaxPoint.Y;
            double currentY = minY;
            double previousOffset = 0;

            while (currentY < maxY)
            {
                double effectiveHeight = Math.Min(TILE_HEIGHT, maxY - currentY);
                if (effectiveHeight < 150) break; // слишком узкий ряд — пропускаем
                var rowResult = FindOptimalRowOffset(facade, windows, currentY, previousOffset, availableRemnants, minOverlap, maxOverlap,
                    rowHeight: effectiveHeight);
                results.Add(rowResult);
                previousOffset = rowResult.Offset;
                currentY += effectiveHeight;
            }

            return results;
        }

        /// <summary>
        /// Анализирует результаты оптимизации и возвращает статистику
        /// </summary>
        public static string AnalyzeOptimizationResults(List<OptimizationResult> results)
        {
            if (!results.Any())
                return "Нет данных для анализа";

            double totalWaste = results.Sum(r => r.TotalWasteArea);
            int totalTiles = results.Sum(r => r.TilesUsed);
            double avgUtilization = results.Average(r => r.UtilizationRatio);
            
            return $"Оптимизация завершена:\n" +
                   $"Всего использовано плит: {totalTiles}\n" +
                   $"Общие отходы: {totalWaste / 1000000:F2} м²\n" +
                   $"Средний коэффициент использования: {avgUtilization:P1}\n" +
                   $"Обработано рядов: {results.Count}";
        }
    }
}
