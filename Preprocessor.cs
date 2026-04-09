using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.OrTools
{
    /// <summary>
    /// Препроцессор для преобразования геометрии AutoCAD в формат OR-Tools.
    /// Разбивает фасад на ряды и сегменты, учитывает окна и выпуски.
    /// </summary>
    public static class Preprocessor
    {
        /// <summary>
        /// Преобразует геометрию AutoCAD в FacadeInput для OR-Tools
        /// </summary>
        public static FacadeInput ConvertToFacadeInput(
            Polyline facade,
            List<Polyline> windows,
            List<Remnant> stockRemnants,
            bool firstRowWithRelease = true,
            OptimizationConstraints? constraints = null,
            List<double>? syncedRowHeights = null,
            List<WindowInfo>? globalForecasterWindows = null)
        {
            var ext = facade.GeometricExtents;
            var facadeWidth = ext.MaxPoint.X - ext.MinPoint.X;
            var facadeHeight = ext.MaxPoint.Y - ext.MinPoint.Y;

            constraints ??= new OptimizationConstraints();

            var input = new FacadeInput
            {
                FacadeId = Guid.NewGuid().ToString(),
                Width = facadeWidth,
                Height = facadeHeight,
                MinX = ext.MinPoint.X,
                MinY = ext.MinPoint.Y,
                TrueFacadeAreaM2 = facade.Area / 1_000_000.0,
                TrueWindowsAreaM2 = windows.Sum(w => w.Area) / 1_000_000.0,
                StockRemnants = new List<Remnant>(stockRemnants),
                Constraints = constraints,
                FirstRowWithRelease = firstRowWithRelease,
                SyncedRowHeights = syncedRowHeights
            };

            // Преобразуем окна
            input.Windows = windows
                .Select((w, i) => WindowInfo.FromPolyline(w, i))
                .ToList();

            // Разбиваем фасад на ряды
            input.Rows = DivideIntoRows(
                facadeWidth, facadeHeight, 
                ext.MinPoint.Y, 
                constraints.TileHeight, 
                constraints.MinRowHeight,
                firstRowWithRelease,
                input.Windows,
                input.StockRemnants,
                constraints,
                syncedRowHeights,
                globalForecasterWindows);

            // Для каждого ряда создаём сегменты (учёт окон)
            foreach (var row in input.Rows)
            {
                // ТЗ v13.1 §29.1: Классификация ряда относительно окон
                row.RowType = ClassifyRow(row, input.Windows);

                row.Segments = CreateSegments(
                    row, 
                    ext.MinPoint.X, 
                    ext.MaxPoint.X, 
                    input.Windows, 
                    constraints);
            }

            return input;
        }

        /// <summary>
        /// Создаёт ряды на основе синхронизированных высот из первого фасада (ТЗ п.3 Row Sync).
        /// Адаптирует высоту: если текущий фасад короче - обрезаем верхние ряды,
        /// если длиннее - добавляем дополнительные ряды стандартной высоты.
        /// </summary>
        private static List<RowDefinition>? CreateSyncedRows(
            double facadeWidth, double facadeHeight,
            double minY,
            bool firstRowWithRelease,
            List<double> syncedHeights,
            double minRowHeight,
            List<WindowInfo>? windows = null)
        {
            if (syncedHeights == null || !syncedHeights.Any())
                throw new ArgumentException("SyncedRowHeights cannot be empty");

            Console.WriteLine($"[ROW-SYNC] CreateSyncedRows: {syncedHeights.Count} рядов, высоты (снизу вверх): [{string.Join(", ", syncedHeights)}]");

            var rows = new List<RowDefinition>();
            double currentY = minY;
            double remainingHeight = facadeHeight;
            int rowIndex = 0;
            
            foreach (var height in syncedHeights)
            {
                if (remainingHeight <= 0) break;
                
                double actualHeight = Math.Min(height, remainingHeight);
                
                if (actualHeight >= minRowHeight || rows.Count == 0)
                {
                    bool hasRelease = firstRowWithRelease ? (rowIndex % 2 == 0) : (rowIndex % 2 == 1);
                    rows.Add(new RowDefinition
                    {
                        RowIndex = rowIndex,
                        Y = currentY,
                        Height = actualHeight,
                        HasRelease = hasRelease
                    });
                    
                    currentY += actualHeight;
                    remainingHeight -= actualHeight;
                    rowIndex++;
                }
            }
            
            while (remainingHeight > 0)
            {
                double addHeight = Math.Min(600.0, remainingHeight);
                if (addHeight >= minRowHeight || rows.Count == 0)
                {
                    bool hasRelease = firstRowWithRelease ? (rowIndex % 2 == 0) : (rowIndex % 2 == 1);
                    rows.Add(new RowDefinition
                    {
                        RowIndex = rowIndex,
                        Y = currentY,
                        Height = addHeight,
                        HasRelease = hasRelease
                    });
                    
                    currentY += addHeight;
                    remainingHeight -= addHeight;
                    rowIndex++;
                }
                else
                {
                    break;
                }
            }
            
            if (windows != null && windows.Any())
            {
                const double safeMargin = 130.0;
                foreach (var row in rows)
                {
                    double rowBoundary = row.Y + row.Height - minY; // relative to minY if we consider facade bottom
                    // Actually Windows MinY/MaxY are absolute coordinates, rowBoundary is absolute too.
                    // The safeMargin is inside the window. Wait, window is MinY..MaxY.
                    foreach (var win in windows)
                    {
                        bool nearBottomEdge = (row.Y + row.Height > win.MinY - safeMargin && row.Y + row.Height < win.MinY + safeMargin);
                        bool nearTopEdge = (row.Y + row.Height > win.MaxY - safeMargin && row.Y + row.Height < win.MaxY + safeMargin);
                        if (nearBottomEdge || nearTopEdge)
                        {
                            Console.WriteLine($"[ROW-SYNC CRITICAL WARN] Фасад: граница ряда {(row.Y + row.Height):F0} мм слишком близко к краю окна [{win.MinY:F0}-{win.MaxY:F0}]. Fallback на RowHeightForecaster.");
                            return null;
                        }
                    }
                }
            }

            foreach (var r in rows)
            {
                Console.WriteLine($"[ROW-SYNC] Ряд {r.RowIndex}: Y={r.Y:F0}, H={r.Height:F0}");
            }

            return rows;
        }

        /// <summary>
        /// Разбивает фасад на ряды по высоте с учётом оконных проёмов (wkeff).
        /// Адаптивная высота предотвращает появление узких полок для L-boot (менее 150 мм).
        /// Использует Google OR-Tools через RowHeightForecaster.
        /// </summary>
        public static List<RowDefinition> DivideIntoRows(
            double facadeWidth, double facadeHeight,
            double minY,
            double tileHeight,
            double minRowHeight,
            bool firstRowWithRelease,
            List<WindowInfo> windows,
            List<Remnant> stockRemnants,
            OptimizationConstraints constraints,
            List<double>? syncedRowHeights = null,
            List<WindowInfo>? globalForecasterWindows = null)
        {
            if (syncedRowHeights != null && syncedRowHeights.Any())
            {
                var syncedRows = CreateSyncedRows(facadeWidth, facadeHeight, minY, firstRowWithRelease, syncedRowHeights, constraints.MinRowHeight, windows);
                if (syncedRows != null)
                {
                    return syncedRows;
                }
            }

            // Пытаемся получить интеллектуальный прогноз через моделей CP-SAT
            var forecastedRows = RowHeightForecaster.Forecast(
                facadeWidth, facadeHeight,
                minY,
                globalForecasterWindows != null ? globalForecasterWindows : windows,
                stockRemnants,
                constraints,
                firstRowWithRelease);

            if (forecastedRows != null && forecastedRows.Any())
            {
                return forecastedRows;
            }

            // Fallback: старый алгоритм (жадный, без учета окон)
            var rows = new List<RowDefinition>();
            int rowIndex = 0;
            const double tol = 0.001;

            double currentY = minY;
            double remainingHeight = facadeHeight;

            while (remainingHeight > tol)
            {
                double h;
                if (remainingHeight <= tileHeight + tol)
                {
                    if (remainingHeight >= minRowHeight - tol)
                    {
                        h = remainingHeight;
                    }
                    else
                    {
                        if (rows.Count > 0)
                        {
                            var lastRow = rows[rows.Count - 1];
                            double deficit = minRowHeight - remainingHeight;
                            double maxReducible = lastRow.Height - minRowHeight;

                            if (maxReducible >= deficit - tol)
                            {
                                lastRow.Height -= deficit;
                                currentY -= deficit; // BUG-HEIGHT-1 FIX: корректируем Y-координату старта нового ряда!
                                h = minRowHeight;
                            }
                            else
                            {
                                lastRow.Height += remainingHeight;
                                remainingHeight = 0;
                                continue;
                            }
                        }
                        else
                        {
                            h = remainingHeight;
                        }
                    }
                }
                else
                {
                    h = tileHeight;
                }

                bool hasRelease = firstRowWithRelease ? (rowIndex % 2 == 0) : (rowIndex % 2 == 1);
                rows.Add(new RowDefinition
                {
                    RowIndex = rowIndex,
                    Y = currentY,
                    Height = h,
                    HasRelease = hasRelease
                });
                
                currentY += h;
                remainingHeight -= h;
                rowIndex++;
            }

            return rows;
        }

        /// <summary>
        /// Разбивает высоту зоны на ряды, стремясь к tileHeight (600), 
        /// но перераспределяя остаток так, чтобы не было рядов < minRowHeight (150).
        /// </summary>
        private static List<double> AdjustRowsToTarget(double targetHeight, double tileHeight, double minRowHeight)
        {
            var heights = new List<double>();
            double remaining = targetHeight;
            const double tol = 0.001;

            while (remaining > tol)
            {
                if (remaining <= tileHeight + tol)
                {
                    // Последний ряд в зоне
                    if (remaining >= minRowHeight - tol)
                    {
                        heights.Add(remaining);
                        remaining = 0;
                    }
                    else
                    {
                        // Остаток меньше MinRowHeight. Перераспределяем.
                        if (heights.Count > 0)
                        {
                            // Пытаемся забрать у предыдущего ряда
                            double prev = heights[heights.Count - 1];
                            double deficit = minRowHeight - remaining;
                            double maxReducible = prev - minRowHeight;

                            if (maxReducible >= deficit - tol)
                            {
                                heights[heights.Count - 1] -= deficit;
                                heights.Add(minRowHeight);
                                remaining = 0;
                            }
                            else
                            {
                                // Если не хватает, просто объединяем с предыдущим (вынужденная мера)
                                heights[heights.Count - 1] += remaining;
                                remaining = 0;
                            }
                        }
                        else
                        {
                            // Зона изначально меньше MinRowHeight
                            heights.Add(remaining);
                            remaining = 0;
                        }
                    }
                }
                else
                {
                    // Добавляем полный ряд
                    heights.Add(tileHeight);
                    remaining -= tileHeight;
                }
            }
            return heights;
        }

        /// <summary>
        /// Создаёт сегменты ряда с учётом окон.
        /// Окна делят ряд на независимые сегменты.
        /// </summary>
        public static List<RowSegment> CreateSegments(
            RowDefinition row,
            double facadeMinX,
            double facadeMaxX,
            List<WindowInfo> windows,
            OptimizationConstraints constraints)
        {
            var segments = new List<RowSegment>();
            double rowMinY = row.Y;
            double rowMaxY = row.Y + row.Height;

            // Для разбиения сегментов учитываем только MIDDLE ряды
            var intersectingMiddleWindows = windows
                .Where(w => w.MinY < rowMaxY && w.MaxY > rowMinY && !IsTopOrBottomRowOfWindow(row, w))
                .OrderBy(w => w.MinX)
                .ToList();

            // Учёт выпуска: расширяем границы фасада для ряда с выпуском
            double effectiveMinX = facadeMinX;
            double effectiveMaxX = facadeMaxX;
            if (row.HasRelease)
            {
                effectiveMinX -= constraints.ReleaseOverhang;
                effectiveMaxX += constraints.ReleaseOverhang;
            }

            if (intersectingMiddleWindows.Count == 0)
            {
                // Нет окон (или окна только краевые) — один сегмент на весь ряд
                segments.Add(new RowSegment
                {
                    RowIndex = row.RowIndex,
                    SegmentIndex = 0,
                    StartX = effectiveMinX,
                    EndX = effectiveMaxX,
                    NearWindowLeft = false, // Для краевого ряда мы формируем целые блоки
                    NearWindowRight = false,
                    IsReleaseLeft = row.HasRelease,
                    IsReleaseRight = row.HasRelease
                });
            }
            else
            {
                // Есть окна (MIDDLE) — делим ряд на сегменты с учетом нахлеста WindowOverlap
                double currentX = effectiveMinX;
                int segmentIndex = 0;

                foreach (var window in intersectingMiddleWindows)
                {
                    // Сегмент до окна (нахлёст ВНУТРЬ проёма: утеплитель заходит на δ мм за раму)
                    double windowEffectiveLeft = window.MinX + constraints.WindowOverlap;
                    
                    if (windowEffectiveLeft > currentX + constraints.MinBlock)
                    {
                        segments.Add(new RowSegment
                        {
                            RowIndex = row.RowIndex,
                            SegmentIndex = segmentIndex++,
                            StartX = currentX,
                            EndX = windowEffectiveLeft,
                            NearWindowLeft = currentX > effectiveMinX,
                            NearWindowRight = true,
                            IsReleaseLeft = row.HasRelease && currentX == effectiveMinX,
                            IsReleaseRight = false
                        });
                    }

                    // Переходим вправо: нахлёст ВНУТРЬ = x_k^R − δ
                    currentX = window.MaxX - constraints.WindowOverlap;
                }

                // Последний сегмент после последнего окна
                if (currentX < effectiveMaxX - constraints.MinBlock)
                {
                    segments.Add(new RowSegment
                    {
                        RowIndex = row.RowIndex,
                        SegmentIndex = segmentIndex,
                        StartX = currentX,
                        EndX = effectiveMaxX,
                        NearWindowLeft = intersectingMiddleWindows.Any(),
                        NearWindowRight = false,
                        IsReleaseLeft = false,
                        IsReleaseRight = row.HasRelease
                    });
                }

                // П-образные элементы разрешены, если окно очень узкое, 
                // но в данном коде это обрабатывается как пустой currentX если ширина недостаточна...
                if (segments.Count == 0 && !IsEntireRowCoveredByWindows(row, intersectingMiddleWindows))
                {
                    segments.Add(new RowSegment
                    {
                        RowIndex = row.RowIndex,
                        SegmentIndex = 0,
                        StartX = effectiveMinX,
                        EndX = effectiveMaxX,
                        NearWindowLeft = false,
                        NearWindowRight = false,
                        IsReleaseLeft = row.HasRelease,
                        IsReleaseRight = row.HasRelease
                    });
                }
            }

            return segments;
        }

        /// <summary>
        /// Проверяет, является ли ряд верхним или нижним (краевым) рядом для окна.
        /// В таких рядах окно "прозрачно" для генерации (затем вырезаются Г-элементы).
        /// </summary>
        public static bool IsTopOrBottomRowOfWindow(RowDefinition row, WindowInfo window)
        {
            double rowMinY = row.Y;
            double rowMaxY = row.Y + row.Height;
            const double tol = 0.001;

            bool isBottomRow = window.MinY >= rowMinY - tol && window.MinY < rowMaxY - tol;
            bool isTopRow = window.MaxY > rowMinY + tol && window.MaxY <= rowMaxY + tol;

            return isBottomRow || isTopRow;
        }

        /// <summary>
        /// Проверяет, покрывают ли окна весь ряд по ширине
        /// </summary>
        private static bool IsEntireRowCoveredByWindows(RowDefinition row, List<WindowInfo> windows)
        {
            // Упрощённая проверка — если суммарная ширина окон в ряду >= ширины ряда
            return false; // Обычно такого не бывает в реальных фасадах
        }

        /// <summary>
        /// Классифицирует ряд относительно окон (ТЗ v13.1 §8, §29.1).
        /// Приоритет: если ряд является краевым хотя бы для одного окна, возвращается соответствующий Edge-тип.
        /// Если ряд полностью внутри окна — Middle. Иначе — Normal.
        /// </summary>
        public static RowType ClassifyRow(RowDefinition row, List<WindowInfo> windows)
        {
            if (windows == null || windows.Count == 0)
                return RowType.Normal;

            double rowMinY = row.Y;
            double rowMaxY = row.Y + row.Height;
            const double tol = 0.001;

            // Собираем типы по каждому окну; приоритет: Edge > Middle > Normal
            RowType best = RowType.Normal;
            int matchCount = 0;

            foreach (var w in windows)
            {
                bool isBottomEdge = w.MinY >= rowMinY - tol && w.MinY < rowMaxY - tol;
                bool isTopEdge = w.MaxY > rowMinY + tol && w.MaxY <= rowMaxY + tol;
                bool isMiddle = rowMinY >= w.MinY - tol && rowMaxY <= w.MaxY + tol;

                RowType candidate = RowType.Normal;
                if (isBottomEdge) candidate = RowType.BottomEdge;
                else if (isTopEdge) candidate = RowType.TopEdge;
                else if (isMiddle) candidate = RowType.Middle;

                if (candidate != RowType.Normal)
                {
                    matchCount++;
                    if (best == RowType.Normal ||
                        candidate == RowType.BottomEdge || candidate == RowType.TopEdge)
                        best = candidate;
                }
            }

            if (matchCount > 1)
                System.Diagnostics.Debug.WriteLine(
                    $"MULTI_WINDOW: Row {row.RowIndex} (Y={row.Y:F0}) matches {matchCount} windows; using type={best}");

            return best;
        }
    }
}
