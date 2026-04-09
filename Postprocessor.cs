using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;
using InsulationMasterPro.Geometry;

namespace InsulationMasterPro.OrTools
{
    /// <summary>
    /// Постпроцессор для преобразования решения OR-Tools в формат AutoCAD.
    /// Создаёт геометрию плиток и обновляет базу остатков.
    /// </summary>
    public static class Postprocessor
    {
        /// <summary>
        /// Преобразует решение OR-Tools в LayoutResult для визуализации в AutoCAD
        /// </summary>
        public static LayoutResult ConvertToLayoutResult(
            OrToolsSolution solution,
            FacadeInput input,
            List<Remnant> originalStock,
            int facadeIndex = 1)
        {
            var result = new LayoutResult
            {
                OptimizationMode = GetOptimizationModeString(solution.Status),
                SolveTimeMs = solution.SolveTimeMs,
                SolverStatus = solution.Status.ToString()
            };

            if (solution.Status != SolverStatus.Optimal && solution.Status != SolverStatus.Feasible)
            {
                result.Errors.Add($"OR-Tools: решение не найдено. Статус: {solution.Status}.");
                if (solution.Diagnostics != null && solution.Diagnostics.Count > 0)
                {
                    foreach (var line in solution.Diagnostics)
                        result.Errors.Add(line);
                }
                return result;
            }

            var usedRemnantIds = new HashSet<Guid>();
            int reusedBlockCounter = 0;

            var blockTiles = new Dictionary<Guid, TileInfo>();
            var suffixMap = new Dictionary<string, int>();

            // Инициализируем карту суффиксов из существующего склада
            foreach (var r in input.StockRemnants)
            {
                if (!string.IsNullOrEmpty(r.BaseBlockNumber))
                {
                    if (!suffixMap.ContainsKey(r.BaseBlockNumber) || suffixMap[r.BaseBlockNumber] < r.SuffixCounter)
                    {
                        suffixMap[r.BaseBlockNumber] = r.SuffixCounter;
                    }
                }
            }

            // Per-row new tile sequence counter (for NNN part of X.Y.NNN)
            var rowTileSeq = new Dictionary<int, int>();
            var pendingReusedTiles = new List<(TileInfo Tile, Block Block, int RowNum)>();
            var stockById = input.StockRemnants
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First());
            var intraById = (solution.IntraFacadeRemnants ?? new List<Remnant>())
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First());
            var stockAfterById = solution.StockAfter
                .GroupBy(r => r.Id)
                .ToDictionary(g => g.Key, g => g.First());

            // Создаём геометрию для каждого блока
            foreach (var row in solution.Rows)
            {
                int rowNum = row.RowIndex + 1; // 1-based for user display
                if (!rowTileSeq.ContainsKey(row.RowIndex))
                    rowTileSeq[row.RowIndex] = 0;

                foreach (var segment in row.Segments)
                {
                    foreach (var block in segment.Blocks)
                    {
                        var tile = CreateTileInfo(block, row, input, solution.LBootElements);
                        tile.RowIndex = row.RowIndex;
                        tile.SegmentIndex = segment.SegmentIndex;
                        bool hasVertices;
                        try { hasVertices = tile.Geometry.NumberOfVertices > 0; }
                        catch (InvalidProgramException) { hasVertices = tile.Width > 0 && tile.Height > 0; }
                        if (hasVertices)
                        {
                            result.AllTiles.Add(tile);
                        }
                        blockTiles[block.Id] = tile;

                        if (block.Type == BlockType.New)
                        {
                            rowTileSeq[row.RowIndex]++;
                            int seq = rowTileSeq[row.RowIndex];
                            tile.BlockNumber = $"{facadeIndex}.{rowNum}.{seq:D3}";
                            tile.ParentInfo = $"Новая плита {tile.BlockNumber}";
                            result.NewTilesCount++;
                            result.NewTilesArea += tile.Width * tile.Height / 1_000_000.0;
                        }
                        else
                        {
                            reusedBlockCounter++;
                            pendingReusedTiles.Add((tile, block, rowNum));

                            result.ReusedRemnantsCount++;
                            result.ReusedArea += tile.Width * tile.Height / 1_000_000.0;
                            if (block.RemnantId.HasValue)
                            {
                                usedRemnantIds.Add(block.RemnantId.Value);
                            }
                        }
                    }
                }
            }

            // Вторая фаза: нумеруем reused-блоки после построения полной карты blockTiles.
            // Это устраняет ложные "без истории", когда parentTile ещё не был обработан в первом проходе.
            var unresolved = new List<(TileInfo Tile, Block Block, int RowNum)>(pendingReusedTiles);
            int maxPasses = Math.Max(1, unresolved.Count);
            for (int pass = 0; pass < maxPasses && unresolved.Count > 0; pass++)
            {
                bool resolvedAny = false;
                for (int i = unresolved.Count - 1; i >= 0; i--)
                {
                    var (tile, block, rowNum) = unresolved[i];
                    string baseBlockStr = string.Empty;
                    int suff = 0;

                    stockById.TryGetValue(block.RemnantId ?? Guid.Empty, out var originalRemnant);
                    if (originalRemnant != null)
                    {
                        tile.SourceRemnantWidth = originalRemnant.Width;
                        tile.SourceRemnantHeight = originalRemnant.Height;
                        if (!string.IsNullOrEmpty(originalRemnant.BaseBlockNumber))
                        {
                            baseBlockStr = originalRemnant.BaseBlockNumber;
                            suff = originalRemnant.SuffixCounter;
                        }
                    }

                    if (string.IsNullOrEmpty(baseBlockStr))
                    {
                        intraById.TryGetValue(block.RemnantId ?? Guid.Empty, out var intraRemnant);
                        if (intraRemnant != null)
                        {
                            tile.SourceRemnantWidth = intraRemnant.Width;
                            tile.SourceRemnantHeight = intraRemnant.Height;
                            tile.IsIntraRowConsolidated = true;

                            if (intraRemnant.SourceBlockId.HasValue &&
                                blockTiles.TryGetValue(intraRemnant.SourceBlockId.Value, out var parentTile) &&
                                !string.IsNullOrEmpty(parentTile.BlockNumber))
                            {
                                var parentBase = ExtractBaseBlockNumber(parentTile.BlockNumber);
                                if (!string.IsNullOrEmpty(parentBase))
                                {
                                    if (string.IsNullOrEmpty(intraRemnant.BaseBlockNumber))
                                    {
                                        if (!suffixMap.ContainsKey(parentBase))
                                            suffixMap[parentBase] = 0;
                                        suffixMap[parentBase]++;
                                        intraRemnant.BaseBlockNumber = parentBase;
                                        intraRemnant.SuffixCounter = suffixMap[parentBase];
                                    }

                                    baseBlockStr = intraRemnant.BaseBlockNumber;
                                    suff = intraRemnant.SuffixCounter;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(baseBlockStr))
                    {
                        stockAfterById.TryGetValue(block.RemnantId ?? Guid.Empty, out var remnantAfter);
                        if (remnantAfter != null)
                        {
                            if (tile.SourceRemnantWidth <= 0) tile.SourceRemnantWidth = remnantAfter.Width;
                            if (tile.SourceRemnantHeight <= 0) tile.SourceRemnantHeight = remnantAfter.Height;

                            if (remnantAfter.SourceBlockId.HasValue &&
                                blockTiles.TryGetValue(remnantAfter.SourceBlockId.Value, out var parentTile) &&
                                !string.IsNullOrEmpty(parentTile.BlockNumber))
                            {
                                var parentBase = ExtractBaseBlockNumber(parentTile.BlockNumber);
                                if (!string.IsNullOrEmpty(parentBase))
                                {
                                    if (!suffixMap.ContainsKey(parentBase))
                                        suffixMap[parentBase] = 0;
                                    suffixMap[parentBase]++;
                                    baseBlockStr = parentBase;
                                    suff = suffixMap[parentBase];
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(baseBlockStr))
                    {
                        tile.BlockNumber = $"{baseBlockStr}.{suff:D2}";
                        tile.ParentInfo = $"Остаток от {baseBlockStr}";

                        unresolved.RemoveAt(i);
                        resolvedAny = true;
                    }
                }

                if (!resolvedAny)
                    break;
            }

            // Неразрешимые случаи действительно без истории происхождения.
            foreach (var (tile, block, rowNum) in unresolved)
            {
                if (!rowTileSeq.ContainsKey(tile.RowIndex))
                    rowTileSeq[tile.RowIndex] = 0;
                rowTileSeq[tile.RowIndex]++;
                int seq = rowTileSeq[tile.RowIndex];
                tile.BlockNumber = $"{facadeIndex}.{rowNum}.{seq:D3}";
                bool fromInitialStock = stockById.ContainsKey(block.RemnantId ?? Guid.Empty);
                tile.ParentInfo = fromInitialStock
                    ? "Остаток со склада (без истории)"
                    : "Остаток от текущей раскладки";
                System.Diagnostics.Debug.WriteLine(fromInitialStock
                    ? $"[FIX] Reused remnant {block.RemnantId} has no BaseBlockNumber — assigned {tile.BlockNumber}"
                    : $"[FIX] Reused remnant {block.RemnantId} unresolved in-session lineage — assigned {tile.BlockNumber}");
            }

            result.TotalInsulationArea = result.NewTilesArea + result.ReusedArea;

            // Собираем созданные остатки (включая переиспользованные — для полной статистики)
            var seenRemnantIds = new HashSet<Guid>();
            foreach (var remnantAfter in solution.StockAfter)
            {
                if (!seenRemnantIds.Add(remnantAfter.Id)) continue;

                string baseBlockNumber = string.Empty;
                int suffixCounter = 0;

                // CreatedAndReused: remnant was already numbered during tile processing via IntraFacadeRemnants
                bool alreadyNumbered = false;
                if (remnantAfter.Status == RemnantStatus.CreatedAndReused)
                {
                    var intra = solution.IntraFacadeRemnants?.FirstOrDefault(r => r.Id == remnantAfter.Id);
                    if (intra != null && !string.IsNullOrEmpty(intra.BaseBlockNumber))
                    {
                        baseBlockNumber = intra.BaseBlockNumber;
                        suffixCounter = intra.SuffixCounter;
                        alreadyNumbered = true;
                    }
                }

                if (!alreadyNumbered && remnantAfter.SourceBlockId.HasValue 
                    && blockTiles.TryGetValue(remnantAfter.SourceBlockId.Value, out var parentTile))
                {
                    baseBlockNumber = ExtractBaseBlockNumber(parentTile.BlockNumber);
                    if (!suffixMap.ContainsKey(baseBlockNumber))
                        suffixMap[baseBlockNumber] = 0;
                    suffixMap[baseBlockNumber]++;
                    suffixCounter = suffixMap[baseBlockNumber];
                }

                if (remnantAfter.Status == RemnantStatus.CreatedFromNew || 
                    remnantAfter.Status == RemnantStatus.CreatedFromCut)
                {
                    result.CreatedRemnants.Add(new Remnant
                    {
                        Id = remnantAfter.Id,
                        Width = remnantAfter.Width,
                        Height = remnantAfter.Height,
                        BaseBlockNumber = baseBlockNumber,
                        SuffixCounter = suffixCounter,
                        FacadeIndex = facadeIndex,
                        Source = remnantAfter.Status == RemnantStatus.CreatedFromNew 
                            ? "Раскрой новой плиты" 
                            : "Раскрой остатка",
                        AddedDate = DateTime.Now
                    });
                }
                else if (remnantAfter.Status == RemnantStatus.CreatedAndReused)
                {
                    result.CreatedRemnants.Add(new Remnant
                    {
                        Id = remnantAfter.Id,
                        Width = remnantAfter.Width,
                        Height = remnantAfter.Height,
                        BaseBlockNumber = baseBlockNumber,
                        SuffixCounter = suffixCounter,
                        FacadeIndex = facadeIndex,
                        Source = "Создан и переиспользован",
                        AddedDate = DateTime.Now
                    });
                }
            }

            // Вычисляем отходы (ширинные + высотные)
            double wasteFromNew = 0;
            double wasteFromRemnants = 0;

            foreach (var row in solution.Rows)
            {
                // BUG-3 fix: track new tiles already counted for height waste in this row
                int newTileCountInRow = 0;

                foreach (var segment in row.Segments)
                {
                    foreach (var block in segment.Blocks)
                    {
                        if (block.Type == BlockType.New)
                        {
                            // Width waste
                            double leftover = input.Constraints.TileWidth - block.Width;
                            if (leftover > 0 && leftover < input.Constraints.MinRemnantToSave)
                            {
                                wasteFromNew += leftover * row.Height / 1_000_000.0;
                            }
                            newTileCountInRow++;
                        }
                        else if (block.Type == BlockType.CutRemnant)
                        {
                            double leftover = block.CutFrom - block.Width;
                            if (leftover > 0 && leftover < input.Constraints.MinRemnantToSave)
                            {
                                wasteFromRemnants += leftover * row.Height / 1_000_000.0;
                            }
                        }
                    }
                }

                // BUG-3 fix: Height waste — when row is shorter than TileHeight and leftover < MinRemnantToSave
                if (row.Height < input.Constraints.TileHeight - 0.001 && newTileCountInRow > 0)
                {
                    double heightLeftover = input.Constraints.TileHeight - row.Height;
                    if (heightLeftover > 0 && heightLeftover < input.Constraints.MinRemnantToSave)
                    {
                        // Each new tile in this row produces a height waste strip
                        wasteFromNew += heightLeftover * input.Constraints.TileWidth * newTileCountInRow / 1_000_000.0;
                    }
                }
            }

            result.WasteFromNewTilesArea = wasteFromNew;
            result.WasteFromRemnantsArea = wasteFromRemnants;
            result.WasteArea = wasteFromNew + wasteFromRemnants;

            // Материальный баланс
            double tileAreaM2 = input.Constraints.TileWidth * input.Constraints.TileHeight / 1_000_000.0;
            result.TotalNewMaterialArea = result.NewTilesCount * tileAreaM2;
            
            result.FacadeGrossArea = input.Width * input.Height / 1_000_000.0;
            result.WindowsCount = input.Windows.Count;
            result.WindowsArea = input.Windows.Sum(w => w.Width * w.Height) / 1_000_000.0;

            result.CreatedRemnantsArea = result.CreatedRemnants
                .DistinctBy(r => r.Id)
                .Where(r => r.Source == "Раскрой новой плиты" || r.Source == "Раскрой остатка")
                .Sum(r => r.Width * r.Height / 1_000_000.0);

            // Предупреждение при превышении 7%:
            result.OverConsumptionExceeded = result.OverconsumptionPct > 7.0;
            if (result.OverConsumptionExceeded)
            {
                result.Errors.Add($"E_OVERCONSUMPTION: {result.OverconsumptionPct:F1}% > 7% [лимит]");
            }

            System.Diagnostics.Debug.WriteLine(
                $"[BALANCE] Facade={result.FacadeGrossArea:F3}m² Windows={result.WindowsArea:F3}m² " +
                $"Net={result.NetInsulationArea:F3}m² NewMat={result.TotalNewMaterialArea:F3}m² " +
                $"Overconsumption=+{result.OverconsumptionPct:F1}%");

            double expected = result.TotalNewMaterialArea;
            double actual   = result.NewTilesArea
                            + result.WasteFromNewTilesArea
                            + result.CreatedRemnantsArea;
            double gap = Math.Abs(expected - actual);

            if (gap > 0.01)
                System.Diagnostics.Debug.WriteLine($"BALANCE_GAP: {gap:F4} м² не учтено в балансе");

            // ТЗ v13.1 §29.6: Валидация оконных узлов
            ValidateMiddleRowsEmpty(solution, input, result);
            ValidateLBootShelves(solution, result);
            ValidateWindowPerimeterCoverage(solution, input, result);
            ValidateOverlapBoundary(solution, input, result);

            return result;
        }

        /// <summary>
        /// ТЗ v13.1 §28.1: Проверяет, что в MIDDLE-рядах нет блоков внутри оконного проёма.
        /// Код ошибки: E_MIDDLE_ROW_WINDOW_ZONE_FILLED
        /// </summary>
        private static void ValidateMiddleRowsEmpty(OrToolsSolution solution, FacadeInput input, LayoutResult result)
        {
            if (input.Windows == null || input.Windows.Count == 0) return;
            const double tol = 0.001;

            foreach (var row in solution.Rows)
            {
                var rowDef = input.Rows.FirstOrDefault(r => r.RowIndex == row.RowIndex);
                if (rowDef == null || rowDef.RowType != RowType.Middle) continue;

                foreach (var w in input.Windows)
                {
                    // BUG-4 fix: overlap INSIDE window (MinX+δ, MaxX-δ), not outside
                    double effLeft = w.MinX + input.Constraints.WindowOverlap;
                    double effRight = w.MaxX - input.Constraints.WindowOverlap;

                    // Проверяем, что ряд действительно Middle относительно этого окна
                    double rowMinY = rowDef.Y;
                    double rowMaxY = rowDef.Y + rowDef.Height;
                    if (!(rowMinY >= w.MinY - tol && rowMaxY <= w.MaxY + tol)) continue;

                    foreach (var seg in row.Segments)
                    {
                        foreach (var block in seg.Blocks)
                        {
                            double bLeft = block.X;
                            double bRight = block.X + block.Width;
                            if (bRight > effLeft + tol && bLeft < effRight - tol)
                            {
                                result.Warnings.Add(
                                    $"E_MIDDLE_ROW_WINDOW_ZONE_FILLED: Ряд {row.RowIndex}, блок X={bLeft:F0}..{bRight:F0} " +
                                    $"пересекает зону окна {w.Id} [{effLeft:F0}..{effRight:F0}]");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// ТЗ v13.1 §28.3: Проверяет, что полки L-boot элементов ≥ 150 мм.
        /// Код ошибки: E_LBOOT_SHELF_SIZE
        /// </summary>
        private static void ValidateLBootShelves(OrToolsSolution solution, LayoutResult result)
        {
            const double minShelf = 150.0;
            const double tol = 0.001;

            foreach (var lboot in solution.LBootElements)
            {
                if (lboot.ShelfH < minShelf - tol && lboot.ShelfH > tol)
                {
                    result.Errors.Add(
                        $"E_LBOOT_SHELF_SIZE: L-boot {lboot.Corner} окна {lboot.WindowId}, ряд {lboot.RowIndex}: " +
                        $"горизонтальная полка {lboot.ShelfH:F1} мм < {minShelf} мм");
                }
                if (lboot.ShelfV < minShelf - tol && lboot.ShelfV > tol)
                {
                    result.Errors.Add(
                        $"E_LBOOT_SHELF_SIZE: L-boot {lboot.Corner} окна {lboot.WindowId}, ряд {lboot.RowIndex}: " +
                        $"вертикальная полка {lboot.ShelfV:F1} мм < {minShelf} мм");
                }
            }
        }

        /// <summary>
        /// ТЗ v13.1 §15 (C17a): Проверяет непрерывное покрытие периметра окна.
        /// Код ошибки: E_WINDOW_PERIMETER_NOT_COVERED
        /// </summary>
        private static void ValidateWindowPerimeterCoverage(OrToolsSolution solution, FacadeInput input, LayoutResult result)
        {
            if (input.Windows == null || input.Windows.Count == 0) return;
            const double tol = 0.001;

            foreach (var w in input.Windows)
            {
                double effLeft = w.MinX - input.Constraints.WindowOverlap;
                double effRight = w.MaxX + input.Constraints.WindowOverlap;

                // Проверяем левую и правую боковины (MIDDLE-ряды должны иметь блоки у откоса)
                int middleRowCount = 0;
                int coveredLeftCount = 0;
                int coveredRightCount = 0;

                foreach (var row in solution.Rows)
                {
                    var rowDef = input.Rows.FirstOrDefault(r => r.RowIndex == row.RowIndex);
                    if (rowDef == null) continue;

                    double rowMinY = rowDef.Y;
                    double rowMaxY = rowDef.Y + rowDef.Height;
                    bool isMiddle = rowMinY >= w.MinY - tol && rowMaxY <= w.MaxY + tol;

                    if (!isMiddle) continue;
                    middleRowCount++;

                    bool hasBlockLeft = false;
                    bool hasBlockRight = false;

                    foreach (var seg in row.Segments)
                    {
                        foreach (var block in seg.Blocks)
                        {
                            // Блок касается левого откоса
                            if (block.X + block.Width >= effLeft - tol && block.X < effLeft - tol)
                                hasBlockLeft = true;
                            // Блок касается правого откоса
                            if (block.X <= effRight + tol && block.X + block.Width > effRight + tol)
                                hasBlockRight = true;
                        }
                    }

                    if (hasBlockLeft) coveredLeftCount++;
                    if (hasBlockRight) coveredRightCount++;
                }

                if (middleRowCount > 0 && coveredLeftCount < middleRowCount)
                {
                    result.Warnings.Add(
                        $"E_WINDOW_PERIMETER_NOT_COVERED: Окно {w.Id} — левая боковина покрыта {coveredLeftCount}/{middleRowCount} рядов");
                }
                if (middleRowCount > 0 && coveredRightCount < middleRowCount)
                {
                    result.Warnings.Add(
                        $"E_WINDOW_PERIMETER_NOT_COVERED: Окно {w.Id} — правая боковина покрыта {coveredRightCount}/{middleRowCount} рядов");
                }
            }
        }

        /// <summary>
        /// Проверяет точное совпадение границы ряда с эффективными границами окна wkeff.bottom / wkeff.top.
        /// Код ошибки: E_OVERLAP_IGNORED
        /// </summary>
        private static void ValidateOverlapBoundary(OrToolsSolution solution, FacadeInput input, LayoutResult result)
        {
            if (input.Windows == null || input.Windows.Count == 0) return;
            const double tol = 0.001;

            double facadeMinY = input.MinY;
            if (input.Windows.Any(w => w.MinY < facadeMinY))
            {
                // Окна ниже фасада в мировых координатах — пересчитываем базу
                facadeMinY = Math.Min(input.MinY, input.Windows.Min(w => w.MinY));
                result.Warnings.Add(
                    $"E_COORD_SYSTEM: input.MinY={input.MinY:F0} выше минY окон={input.Windows.Min(w => w.MinY):F0}. " +
                    $"Используется скорректированная база facadeMinY={facadeMinY:F0}. " +
                    $"Проверьте Preprocessor — координаты должны нормализоваться в [0..H_facade].");
            }

            foreach (var w in input.Windows)
            {
                double wkeffBottom = w.MinY - facadeMinY;  // должно быть > 0
                double wkeffTop    = w.MaxY - facadeMinY;  // должно быть < H_facade

                if (wkeffBottom < 0 || wkeffTop < 0)
                {
                    result.Warnings.Add(
                        $"E_OVERLAP_COORD_ERROR: Окно {w.Id} — отрицательные координаты после нормализации: " +
                        $"bottom={wkeffBottom:F1}мм, top={wkeffTop:F1}мм. Проверка швов пропущена для этого окна.");
                    continue;
                }

                double effBottom = w.MinY + input.Constraints.WindowOverlap;
                double effTop = w.MaxY - input.Constraints.WindowOverlap;

                bool hasBottomSeam = false;
                bool hasTopSeam = false;

                foreach (var rowDef in input.Rows)
                {
                    if (Math.Abs(rowDef.Y - effBottom) < tol || Math.Abs(rowDef.Y + rowDef.Height - effBottom) < tol)
                        hasBottomSeam = true;

                    if (Math.Abs(rowDef.Y - effTop) < tol || Math.Abs(rowDef.Y + rowDef.Height - effTop) < tol)
                        hasTopSeam = true;
                }

                if (!hasBottomSeam)
                {
                    result.Warnings.Add($"E_OVERLAP_IGNORED: Горизонтальный шов фасада не соответствует wkeff.bottom ({wkeffBottom + input.Constraints.WindowOverlap:F1} мм) для окна {w.Id}. Ряд выступает за проём.");
                }
                if (!hasTopSeam)
                {
                    result.Warnings.Add($"E_OVERLAP_IGNORED: Горизонтальный шов фасада не соответствует wkeff.top ({wkeffTop - input.Constraints.WindowOverlap:F1} мм) для окна {w.Id}. Возможно перекрытие окна материалом.");
                }
            }
        }

        /// <summary>
        /// Создаёт TileInfo для блока. Если блок является L-boot элементом,
        /// строится Г-образная Polyline (6 вершин); иначе — прямоугольник (4 вершины).
        /// ТЗ v13.1 §14: L-boot визуализация через Boolean Subtraction.
        /// </summary>
        private static TileInfo CreateTileInfo(Block block, RowSolution row, FacadeInput input, List<LBootElement> lBootElements)
        {
            const double tol = 0.5;
            double bx = block.X;
            double bw = block.Width;
            double ry = row.Y;
            double rh = row.Height;

            // Ищем L-boot элемент, соответствующий данному блоку
            LBootElement matchedLBoot = null;
            if (lBootElements != null)
            {
                matchedLBoot = lBootElements.FirstOrDefault(lb =>
                    lb.RowIndex == row.RowIndex &&
                    Math.Abs(lb.SourceBlockX - bx) < tol &&
                    Math.Abs(lb.SourceBlockWidth - bw) < tol &&
                    lb.CutoutWidth > tol && lb.CutoutHeight > tol);
            }

            var poly = new Polyline();

            if (matchedLBoot != null)
            {
                // Строим Г-образную Polyline (6 вершин)
                double cutW = matchedLBoot.CutoutWidth;
                double cutH = matchedLBoot.CutoutHeight;
                string corner = matchedLBoot.Corner;

                // Координаты полного прямоугольника блока
                double x0 = bx;           // левый край
                double x1 = bx + bw;      // правый край
                double y0 = ry;           // нижний край
                double y1 = ry + rh;      // верхний край

                // Если вырез занимает всю глубину или ширину, это простой прямоугольник, а не Г-форма
                if (Math.Abs(cutW - bw) < tol && Math.Abs(cutH - rh) < tol)
                {
                    // Блок полностью вырезан (хотя он не должен доходить сюда)
                    // poly остается пустым, условие NumberOfVertices > 0 отфильтрует его при добавлении
                }
                else if (Math.Abs(cutW - bw) < tol)
                {
                    // Вырез по всей ширине блока (блок полностью попадает в ширину окна)
                    if (corner == "BL" || corner == "BR")
                    {
                        // Окно сверху (ряд BottomEdge) -> остается нижний прямоугольник
                        poly.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
                        poly.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
                        poly.AddVertexAt(2, new Point2d(x1, y1 - cutH), 0, 0, 0);
                        poly.AddVertexAt(3, new Point2d(x0, y1 - cutH), 0, 0, 0);
                    }
                    else
                    {
                        // Окно снизу (ряд TopEdge) -> остается верхний прямоугольник
                        poly.AddVertexAt(0, new Point2d(x0, y0 + cutH), 0, 0, 0);
                        poly.AddVertexAt(1, new Point2d(x1, y0 + cutH), 0, 0, 0);
                        poly.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
                        poly.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
                    }
                }
                else if (Math.Abs(cutH - rh) < tol)
                {
                    // Вырез по всей высоте блока (крайне редкий случай для EdgeRow, но возможен)
                    if (corner == "BL" || corner == "TL") // Вырез справа
                    {
                        // Остается левый прямоугольник
                        poly.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
                        poly.AddVertexAt(1, new Point2d(x1 - cutW, y0), 0, 0, 0);
                        poly.AddVertexAt(2, new Point2d(x1 - cutW, y1), 0, 0, 0);
                        poly.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
                    }
                    else // Вырез слева
                    {
                        // Остается правый прямоугольник
                        poly.AddVertexAt(0, new Point2d(x0 + cutW, y0), 0, 0, 0);
                        poly.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
                        poly.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
                        poly.AddVertexAt(3, new Point2d(x0 + cutW, y1), 0, 0, 0);
                    }
                }
                else if (corner == "BL")
                {
                    // Вырез в правом верхнем углу блока
                    // Обход против часовой стрелки:
                    //   (x0,y0) → (x1,y0) → (x1, y1-cutH) → (x1-cutW, y1-cutH) → (x1-cutW, y1) → (x0, y1)
                    int v = 0;
                    poly.AddVertexAt(v++, new Point2d(x0, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y1 - cutH), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1 - cutW, y1 - cutH), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1 - cutW, y1), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0, y1), 0, 0, 0);
                }
                else if (corner == "BR")
                {
                    // Вырез в левом верхнем углу блока
                    //   (x0,y0) → (x1,y0) → (x1,y1) → (x0+cutW, y1) → (x0+cutW, y1-cutH) → (x0, y1-cutH)
                    int v = 0;
                    poly.AddVertexAt(v++, new Point2d(x0, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y1), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0 + cutW, y1), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0 + cutW, y1 - cutH), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0, y1 - cutH), 0, 0, 0);
                }
                else if (corner == "TL")
                {
                    // Вырез в правом нижнем углу блока
                    //   (x0,y0) → (x1-cutW, y0) → (x1-cutW, y0+cutH) → (x1, y0+cutH) → (x1,y1) → (x0,y1)
                    int v = 0;
                    poly.AddVertexAt(v++, new Point2d(x0, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1 - cutW, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1 - cutW, y0 + cutH), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y0 + cutH), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y1), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0, y1), 0, 0, 0);
                }
                else if (corner == "TR")
                {
                    // Вырез в левом нижнем углу блока
                    //   (x0, y0+cutH) → (x0+cutW, y0+cutH) → (x0+cutW, y0) → (x1,y0) → (x1,y1) → (x0,y1)
                    int v = 0;
                    poly.AddVertexAt(v++, new Point2d(x0, y0 + cutH), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0 + cutW, y0 + cutH), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0 + cutW, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y0), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x1, y1), 0, 0, 0);
                    poly.AddVertexAt(v++, new Point2d(x0, y1), 0, 0, 0);
                }
                else
                {
                    // Неизвестный угол — fallback на прямоугольник
                    poly.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
                    poly.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
                    poly.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
                    poly.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
                }
            }
            else
            {
                // Обычный прямоугольный блок
                poly.AddVertexAt(0, new Point2d(bx, ry), 0, 0, 0);
                poly.AddVertexAt(1, new Point2d(bx + bw, ry), 0, 0, 0);
                poly.AddVertexAt(2, new Point2d(bx + bw, ry + rh), 0, 0, 0);
                poly.AddVertexAt(3, new Point2d(bx, ry + rh), 0, 0, 0);
            }
            poly.Closed = true;

            // BUG-A fix: вычисляем фактические габариты из Polyline extent
            // (для L-boot/вырезанных блоков poly.Height < row.Height)
            double actualWidth, actualHeight;
            try
            {
                var extent = poly.GeometricExtents;
                actualWidth = Math.Round(extent.MaxPoint.X - extent.MinPoint.X, 1);
                actualHeight = Math.Round(extent.MaxPoint.Y - extent.MinPoint.Y, 1);
            }
            catch (System.InvalidProgramException)
            {
                actualWidth = Math.Round(bw, 1);
                actualHeight = Math.Round(rh, 1);
            }

            var tileInfo = new TileInfo
            {
                Geometry = poly,
                IsReused = block.Type != BlockType.New,
                Width = actualWidth,
                Height = actualHeight,
                SourceRemnantId = block.RemnantId,
                HasRelease = false // Будет определено при анализе позиции
            };

            int verts;
            try { verts = poly.NumberOfVertices; }
            catch (InvalidProgramException) { verts = -1; }
            System.Diagnostics.Debug.WriteLine(
                $"[FIX] CreateTileInfo: W={actualWidth:F0} H={actualHeight:F0} " +
                $"(block.W={block.Width:F0}, row.H={row.Height:F0}, verts={verts})");

            return tileInfo;
        }

        /// <summary>
        /// Обновляет базу остатков на основе решения
        /// </summary>
        public static void UpdateRemnantDatabase(
            OrToolsSolution solution,
            List<Remnant> originalStock,
            Action<Remnant> addRemnant,
            Action<Guid> removeRemnant,
            List<Remnant> preComputedRemnants = null)
        {
            // Удаляем использованные остатки
            var usedIds = new HashSet<Guid>();
            foreach (var row in solution.Rows)
            {
                foreach (var segment in row.Segments)
                {
                    foreach (var block in segment.Blocks)
                    {
                        if (block.RemnantId.HasValue)
                        {
                            usedIds.Add(block.RemnantId.Value);
                        }
                    }
                }
            }

            foreach (var id in usedIds)
            {
                removeRemnant(id);
            }

            // Добавляем созданные остатки (с lineage: BaseBlockNumber и SuffixCounter)
            foreach (var remnantAfter in solution.StockAfter)
            {
                if (remnantAfter.Status == RemnantStatus.CreatedFromNew ||
                    remnantAfter.Status == RemnantStatus.CreatedFromCut)
                {
                    // Ищем BaseBlockNumber из SourceBlockId → blockTiles (если доступны через preComputedRemnants)
                    var preComputed = preComputedRemnants?.FirstOrDefault(r => r.Id == remnantAfter.Id);
                    addRemnant(new Remnant
                    {
                        Id = remnantAfter.Id,
                        Width = remnantAfter.Width,
                        Height = remnantAfter.Height,
                        Source = remnantAfter.Status.ToString(),
                        AddedDate = DateTime.Now,
                        BaseBlockNumber = preComputed?.BaseBlockNumber ?? string.Empty,
                        SuffixCounter = preComputed?.SuffixCounter ?? 0
                    });
                }
            }
        }

        /// <summary>
        /// Генерирует текстовый отчёт о результатах оптимизации
        /// </summary>
        public static string GenerateReport(OrToolsSolution solution, LayoutResult result, FacadeInput input)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine("        ОТЧЁТ РАСКЛАДКИ УТЕПЛИТЕЛЯ (OR-Tools)");
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine();

            sb.AppendLine($"Режим оптимизации: {result.OptimizationMode}");
            sb.AppendLine($"Статус решения: {result.SolverStatus}");
            sb.AppendLine($"Время решения: {result.SolveTimeMs} мс");
            sb.AppendLine();

            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine("ФАСАД");
            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine($"  Размеры: {input.Width:F0} × {input.Height:F0} мм");
            sb.AppendLine($"  Количество рядов: {input.Rows.Count}");
            sb.AppendLine($"  Количество окон: {input.Windows.Count}");
            sb.AppendLine();

            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine("МАТЕРИАЛЫ");
            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine($"  Новых плит: {result.NewTilesCount} шт. ({result.NewTilesArea:F2} м²)");
            sb.AppendLine($"  Использовано остатков: {result.ReusedRemnantsCount} шт. ({result.ReusedArea:F2} м²)");
            sb.AppendLine($"  ИТОГО площадь утепления: {result.TotalInsulationArea:F2} м²");
            sb.AppendLine();

            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine("ОСТАТКИ");
            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine($"  Остатков на складе ДО: {input.StockRemnants.Count} шт. ({input.StockRemnants.Sum(r => r.Area) / 1_000_000.0:F2} м²)");
            sb.AppendLine($"  Остатков на складе ПОСЛЕ: {solution.StockAfter.Count} шт. ({solution.TotalRemainingArea:F2} м²)");
            sb.AppendLine($"  Создано новых остатков: {result.CreatedRemnants.Count} шт.");
            sb.AppendLine();

            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine("ОТХОДЫ");
            sb.AppendLine("─────────────────────────────────────────────────────────");
            sb.AppendLine($"  Отходы от раскроя новых плит: {result.WasteFromNewTilesArea:F3} м²");
            sb.AppendLine($"  Отходы от раскроя остатков: {result.WasteFromRemnantsArea:F3} м²");
            sb.AppendLine($"  ИТОГО отходов: {result.WasteArea:F3} м²");
            sb.AppendLine();

            sb.AppendLine($"  Коэффициент использования: {result.UtilizationRatio * 100:F1}%");
            sb.AppendLine();

            if (result.CreatedRemnants.Count > 0)
            {
                sb.AppendLine("─────────────────────────────────────────────────────────");
                sb.AppendLine("СОЗДАННЫЕ ОСТАТКИ (сохранены в БД)");
                sb.AppendLine("─────────────────────────────────────────────────────────");
                foreach (var remnant in result.CreatedRemnants.Take(20))
                {
                    sb.AppendLine($"  {remnant.Width:F0} × {remnant.Height:F0} мм ({remnant.Source})");
                }
                if (result.CreatedRemnants.Count > 20)
                {
                    sb.AppendLine($"  ... и ещё {result.CreatedRemnants.Count - 20} шт.");
                }
            }

            if (solution.Diagnostics != null && solution.Diagnostics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("─────────────────────────────────────────────────────────");
                sb.AppendLine("ДИАГНОСТИКА (для какого ряда/сегмента решение не найдено)");
                sb.AppendLine("─────────────────────────────────────────────────────────");
                foreach (var line in solution.Diagnostics)
                    sb.AppendLine($"  {line}");
            }

            // ─── ДВИЖЕНИЕ ОСТАТКОВ — трассировка каждого блока ───
            if (result.AllTiles.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("─────────────────────────────────────────────────────────");
                sb.AppendLine("ДВИЖЕНИЕ ОСТАТКОВ (трассировка)");
                sb.AppendLine("─────────────────────────────────────────────────────────");

                // Новые плиты
                var newTiles = result.AllTiles.Where(t => !t.IsReused && !string.IsNullOrEmpty(t.BlockNumber)).ToList();
                if (newTiles.Any())
                {
                    sb.AppendLine($"  Новые плиты ({newTiles.Count} шт.):");
                    foreach (var tile in newTiles.Take(30))
                    {
                        sb.AppendLine($"    {tile.BlockNumber}: {tile.Width:F0}×{tile.Height:F0} мм ← {tile.ParentInfo}");
                    }
                    if (newTiles.Count > 30)
                        sb.AppendLine($"    ... и ещё {newTiles.Count - 30} шт.");
                }

                // Использованные остатки
                var reusedTiles = result.AllTiles.Where(t => t.IsReused && !string.IsNullOrEmpty(t.BlockNumber)).ToList();
                if (reusedTiles.Any())
                {
                    sb.AppendLine($"  Использованные остатки ({reusedTiles.Count} шт.):");
                    foreach (var tile in reusedTiles.Take(30))
                    {
                        string pos = tile.Geometry != null ? $"X={tile.Position.X:F0}" : "";
                        sb.AppendLine($"    {tile.BlockNumber}: {tile.Width:F0}×{tile.Height:F0} мм → {pos} ← {tile.ParentInfo}");
                    }
                    if (reusedTiles.Count > 30)
                        sb.AppendLine($"    ... и ещё {reusedTiles.Count - 30} шт.");
                }

                // Созданные остатки с историей нарезки
                var traced = result.CreatedRemnants.Where(r => !string.IsNullOrEmpty(r.CutHistory)).ToList();
                if (traced.Any())
                {
                    sb.AppendLine($"  История нарезки ({traced.Count} записей):");
                    foreach (var r in traced.Take(20))
                    {
                        sb.AppendLine($"    {r.Width:F0}×{r.Height:F0} мм: {r.CutHistory}");
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════════════════════");
            sb.AppendLine($"Дата/время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            return sb.ToString();
        }

        /// <summary>
        /// Extracts the base block number from a full block number.
        /// New format X.Y.NNN.ZZ → base is X.Y.NNN (first 3 segments).
        /// Legacy format N0027.1 → base is N0027 (first segment).
        /// </summary>
        private static string ExtractBaseBlockNumber(string blockNumber)
        {
            if (string.IsNullOrEmpty(blockNumber)) return blockNumber;
            var parts = blockNumber.Split('.');
            if (parts.Length >= 4)
                return $"{parts[0]}.{parts[1]}.{parts[2]}";
            if (parts.Length == 3)
                return blockNumber; // Already a base: X.Y.NNN
            return parts[0]; // Legacy: N0027
        }

        private static string GetOptimizationModeString(SolverStatus status)
        {
            return status switch
            {
                SolverStatus.Optimal => "OR-Tools (оптимальное решение)",
                SolverStatus.Feasible => "OR-Tools (допустимое решение)",
                SolverStatus.Timeout => "OR-Tools (таймаут)",
                SolverStatus.Infeasible => "OR-Tools (решение не найдено)",
                _ => "OR-Tools (ошибка)"
            };
        }
    }
}
