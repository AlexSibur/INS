using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;
using InsulationMasterPro.Geometry;
using InsulationMasterPro.Data;
using InsulationMasterPro.Validation;
using InsulationMasterPro.Visualization;
using InsulationMasterPro.OrTools;

namespace InsulationMasterPro.Logic
{
    public class LayoutEngine
    {
        private readonly TileSpecification _tileSpec;
        private readonly LayoutParameters _parameters;
        private readonly OrToolsOptimizationEngine? _orToolsEngine;
        
        private List<double>? _syncedRowHeights;
        private bool _rowSyncEnabled = true;

        public LayoutEngine()
        {
            _tileSpec = new TileSpecification();
            _parameters = new LayoutParameters();
            _orToolsEngine = null;
        }

        public LayoutEngine(TileSpecification tileSpec, LayoutParameters parameters)
        {
            _tileSpec = tileSpec;
            _parameters = parameters;
            
            if (_parameters.UseOrTools)
            {
                var config = new OptimizerConfig
                {
                    TimeoutSeconds = _parameters.OrToolsTimeoutSeconds,
                    UseRollingHorizon = _parameters.UseRollingHorizon,
                    RollingHorizonRows = _parameters.RollingHorizonRows,
                    RollingHorizonTimeoutPerWindowSeconds = _parameters.RollingHorizonTimeoutPerWindowSeconds
                };
                var constraints = new OptimizationConstraints
                {
                    TileWidth = _tileSpec.Width,
                    TileHeight = _tileSpec.Height,
                    MinJointOffset = _parameters.MinJointOffset,
                    MinBlock = _parameters.MinRemnantWidth,
                    MinBlockNearWindow = _parameters.MinOpeningRemnantWidth,
                    ReleaseOverhang = _parameters.ReleaseAmount
                };
                _orToolsEngine = new OrToolsOptimizationEngine(config, constraints);
            }
        }

        /// <summary>
        /// Основной метод обработки фасадов
        /// </summary>
        public LayoutResult Process(List<Polyline> facades, List<Polyline> windows)
        {
            var result = new LayoutResult();

            // C9: автоматическая очистка старых/использованных остатков при каждом запуске
            RemnantDatabase.CleanupDatabase(maxAgeDays: 90);

            var context = new ProcessingContext
            {
                Facades = facades,
                Windows = windows,
                AvailableRemnants = RemnantDatabase.LoadRemnants()
            };

            try
            {
                // 1. Валидация контуров
                var validationResult = ValidateInputs(facades, windows);
                if (!validationResult.IsValid)
                {
                    result.Errors.AddRange(validationResult.Errors);
                    return result;
                }
                result.Warnings.AddRange(validationResult.Warnings);

                // 1a. Сортировка фасадов (ТЗ п.2: по Y сверху вниз, затем по X слева направо)
                facades = SortFacadesByPosition(facades);
                context.Facades = facades;

                // 1b. Сохраняем информацию о фасадах для визуальной маркировки (ТЗ п.5)
                result.FacadeInfos = facades.Select((f, i) => new FacadeInfo
                {
                    Index = i + 1,
                    Geometry = f
                }).ToList();

                // 2. Оптимизация базы остатков
                if (_parameters.OptimizeRemnants)
                {
                    context.AvailableRemnants = RemnantManager.OptimizeRemnantDatabase(context.AvailableRemnants);
                }

                // 3. Г-образные элементы ОТМЕНЕНЫ (ТЗ v2.2) - вместо них используется штриховка в оконных рядах
                var lBoots = new List<Polyline>();

                // Вычисляем глобальные проецированные окна для первого фасада
                List<Polyline> globalForecasterWindows = null;
                var facade1Ext = facades[0].GeometricExtents;
                var facade1MinY = facade1Ext.MinPoint.Y;
                var facade1MinX = facade1Ext.MinPoint.X;

                // Для каждого фасада
                int facadeIdx = 0;
                _syncedRowHeights = null;
                foreach (var facade in facades)
                {
                    facadeIdx++;
                    
                    bool firstRowWithRelease = _parameters.FirstRowWithRelease;
                    if (facadeIdx > 1)
                    {
                        firstRowWithRelease = (facadeIdx % 2 == 1) ? _parameters.FirstRowWithRelease : !_parameters.FirstRowWithRelease;
                    }
                    
                    var facadeWindows = windows.Where(w => IsWindowInsideFacade(w, facade)).ToList();
                    
                    // Stock Flush ОТКЛЮЧЁН (v3.7.0): нарушает инвариант углового замыкания.
                    // Все фасады используют Row Sync — одинаковые высоты рядов из Фасада 1.
                    bool stockFlushLastFacade = false;
                    bool isLastFacade = (facadeIdx == facades.Count);
                    bool useStockFlush = stockFlushLastFacade && isLastFacade && facades.Count > 1 && _rowSyncEnabled;
                    var currentSyncedHeights = (facadeIdx > 1 && !useStockFlush) ? _syncedRowHeights : null;

                    if (useStockFlush)
                    {
                        result.Warnings.Add($"Stock Flush: Фасад {facadeIdx} — независимая сетка рядов для максимальной утилизации остатков");
                    }

                    // Если это первый фасад, готовим спроецированные окна для Forecaster
                    List<WindowInfo> forecasterWindows = null;
                    if (facadeIdx == 1)
                    {
                        forecasterWindows = new List<WindowInfo>();
                        int gwIdx = 0;
                        for (int i = 0; i < facades.Count; i++)
                        {
                            var fExt = facades[i].GeometricExtents;
                            var fWins = windows.Where(w => IsWindowInsideFacade(w, facades[i])).ToList();
                            foreach (var w in fWins)
                            {
                                var wExt = w.GeometricExtents;
                                double relMinY = wExt.MinPoint.Y - fExt.MinPoint.Y;
                                double relMaxY = wExt.MaxPoint.Y - fExt.MinPoint.Y;
                                forecasterWindows.Add(new WindowInfo
                                {
                                    Id = $"global_w{gwIdx++}",
                                    MinX = wExt.MinPoint.X - fExt.MinPoint.X + facade1MinX,
                                    MaxX = wExt.MaxPoint.X - fExt.MinPoint.X + facade1MinX,
                                    MinY = relMinY + facade1MinY,
                                    MaxY = relMaxY + facade1MinY
                                });
                            }
                        }
                    }
                    
                    var facadeResult = ProcessSingleFacade(
                        facade, facadeWindows, lBoots, context, facadeIdx, 
                        firstRowWithRelease, currentSyncedHeights, forecasterWindows);
                    MergeResults(result, facadeResult);
                    
                    if (facadeIdx == 1 && _rowSyncEnabled)
                    {
                        _syncedRowHeights = ExtractRowHeights(facadeResult);
                        result.Warnings.Add($"Row Sync: сохранено {_syncedRowHeights.Count} высот рядов для синхронизации");
                    }
                }

                // 5. Обработка отходов и сохранение в БД
                ProcessWasteAndSaveRemnants(context, result);

                // 6. Финальная статистика
                FinalizeStatistics(result);

                result.Warnings.Add($"Обработка завершена за {context.ProcessingTime.TotalSeconds:F1} сек");
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Критическая ошибка при обработке: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Сортировка фасадов по положению (ТЗ п.2).
        /// Сначала группировка по Y (допуск 10мм), затем по X внутри группы.
        /// Фасад №1 - самый верхний левый.
        /// </summary>
        private List<Polyline> SortFacadesByPosition(List<Polyline> facades)
        {
            if (facades.Count <= 1) return facades;

            const double yTolerance = 10.0;

            var facadesWithCenter = facades.Select(f =>
            {
                var ext = f.GeometricExtents;
                return new
                {
                    Polyline = f,
                    CenterY = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2,
                    CenterX = (ext.MinPoint.X + ext.MaxPoint.X) / 2
                };
            }).ToList();

            var grouped = facadesWithCenter
                .GroupBy(fc => fc.CenterY, new YGroupComparer(yTolerance))
                .OrderByDescending(g => g.Key)
                .ToList();

            var sorted = new List<Polyline>();
            foreach (var group in grouped)
            {
                var orderedInGroup = group.OrderBy(fc => fc.CenterX).ToList();
                sorted.AddRange(orderedInGroup.Select(fc => fc.Polyline));
            }

            return sorted;
        }

        private class YGroupComparer : IEqualityComparer<double>
        {
            private readonly double _tolerance;
            public YGroupComparer(double tolerance) { _tolerance = tolerance; }
            public bool Equals(double x, double y) => Math.Abs(x - y) < _tolerance;
            public int GetHashCode(double obj) => 0;
        }

        /// <summary>
        /// Извлекает высоты рядов из результата раскладки первого фасада для синхронизации.
        /// </summary>
        private List<double> ExtractRowHeights(LayoutResult result)
        {
            var heights = new List<double>();
            if (result.AllTiles == null || !result.AllTiles.Any()) return heights;
            
            var rows = result.AllTiles
                .GroupBy(t => t.RowIndex)
                .OrderBy(g => g.Key)
                .ToList();
                
            foreach (var row in rows)
            {
                double maxY = row.Max(t => t.Position.Y + t.Height);
                double minY = row.Min(t => t.Position.Y);
                heights.Add(maxY - minY);
            }
            
            return heights;
        }

        /// <summary>
        /// Валидация входных данных
        /// </summary>
        private InsulationMasterPro.Validation.ValidationResult ValidateInputs(List<Polyline> facades, List<Polyline> windows)
        {
            var result = new InsulationMasterPro.Validation.ValidationResult();

            // Валидация фасадов
            var facadeValidation = ValidationEngine.ValidateMultipleContours(facades, "Фасад");
            result.Errors.AddRange(facadeValidation.Errors);
            result.Warnings.AddRange(facadeValidation.Warnings);

            // Валидация окон
            var windowValidation = ValidationEngine.ValidateMultipleContours(windows, "Окно");
            result.Errors.AddRange(windowValidation.Errors);
            result.Warnings.AddRange(windowValidation.Warnings);

            // Проверка пересечений фасадов и окон (только при наличии окон)
            if (windows.Count > 0)
            {
                for (int i = 0; i < facades.Count; i++)
                {
                    for (int j = 0; j < windows.Count; j++)
                    {
                        if (!IsWindowInsideFacade(windows[j], facades[i]))
                        {
                            result.Warnings.Add($"Окно #{j + 1} может находиться вне границ фасада #{i + 1}");
                        }
                    }
                }
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Обработка одного фасада. Используется только OR-Tools (жадный алгоритм удалён).
        /// </summary>
        private LayoutResult ProcessSingleFacade(Polyline facade, List<Polyline> windows, List<Polyline> lBoots, ProcessingContext context, int facadeIndex = 1, bool firstRowWithRelease = true, List<double>? syncedRowHeights = null, List<WindowInfo>? globalForecasterWindows = null)
        {
            if (!_parameters.UseOrTools || _orToolsEngine == null)
            {
                var err = new LayoutResult();
                err.Errors.Add("Раскладка выполняется только через OR-Tools. Убедитесь, что UseOrTools включён при создании LayoutEngine.");
                return err;
            }
            return ProcessWithOrTools(facade, windows, context, facadeIndex, firstRowWithRelease, syncedRowHeights, globalForecasterWindows);
        }

        /// <summary>
        /// Обработка фасада с использованием OR-Tools глобальной оптимизации.
        /// Минимизирует суммарную площадь остатков на складе.
        /// </summary>
        private LayoutResult ProcessWithOrTools(Polyline facade, List<Polyline> windows, ProcessingContext context, int facadeIndex = 1, bool firstRowWithRelease = true, List<double>? syncedRowHeights = null, List<WindowInfo>? globalForecasterWindows = null)
        {
            if (_orToolsEngine == null)
                throw new InvalidOperationException("OR-Tools engine is not initialized");

            // Вызываем OR-Tools оптимизатор
            var solution = _orToolsEngine.Optimize(
                facade, 
                windows, 
                context.AvailableRemnants,
                firstRowWithRelease,
                syncedRowHeights,
                globalForecasterWindows);

            // Конвертируем в FacadeInput для Postprocessor
            var input = Preprocessor.ConvertToFacadeInput(
                facade, windows, context.AvailableRemnants, 
                firstRowWithRelease, 
                new OptimizationConstraints
                {
                    TileWidth = _tileSpec.Width,
                    TileHeight = _tileSpec.Height,
                    MinJointOffset = _parameters.MinJointOffset,
                    MinBlock = _parameters.MinRemnantWidth,
                    MinBlockNearWindow = _parameters.MinOpeningRemnantWidth,
                    ReleaseOverhang = _parameters.ReleaseAmount
                },
                syncedRowHeights,
                globalForecasterWindows);

            input.FacadeIndex = facadeIndex;

            // Конвертируем решение в LayoutResult
            var result = Postprocessor.ConvertToLayoutResult(solution, input, context.AvailableRemnants, facadeIndex);
            result.DiagInput = input;
            result.DiagSolution = solution;

            // Обновляем контекст: удаляем использованные остатки, добавляем новые
            var usedIds = new HashSet<Guid>();
            foreach (var row in solution.Rows)
            {
                foreach (var segment in row.Segments)
                {
                    foreach (var block in segment.Blocks)
                    {
                        if (block.RemnantId.HasValue)
                            usedIds.Add(block.RemnantId.Value);
                    }
                }
            }

            context.UsedRemnantIds.UnionWith(usedIds);
            context.AvailableRemnants.RemoveAll(r => usedIds.Contains(r.Id));
            context.AvailableRemnants.AddRange(result.CreatedRemnants);

            return result;
        }

        /// <summary>
        /// Создание Г-образных элементов для окон
        /// </summary>
        private List<Polyline> CreateLBootsForWindows(List<Polyline> windows)
        {
            var lBoots = new List<Polyline>();
            
            foreach (var window in windows)
            {
                var windowBoots = LBootGenerator.CreateLBoots(window, _tileSpec.Width, _tileSpec.Height);
                lBoots.AddRange(windowBoots);
            }
            
            return lBoots;
        }

        /// <summary>
        /// Проверка, находится ли окно внутри фасада
        /// </summary>
        private bool IsWindowInsideFacade(Polyline window, Polyline facade)
        {
            var windowExtents = window.GeometricExtents;
            var facadeExtents = facade.GeometricExtents;
            
            return windowExtents.MinPoint.X >= facadeExtents.MinPoint.X &&
                   windowExtents.MaxPoint.X <= facadeExtents.MaxPoint.X &&
                   windowExtents.MinPoint.Y >= facadeExtents.MinPoint.Y &&
                   windowExtents.MaxPoint.Y <= facadeExtents.MaxPoint.Y;
        }

        /// <summary>
        /// Объединение результатов
        /// </summary>
        private void MergeResults(LayoutResult mainResult, LayoutResult partialResult)
        {
            mainResult.NewTilesCount += partialResult.NewTilesCount;
            mainResult.NewTilesArea += partialResult.NewTilesArea;
            mainResult.ReusedRemnantsCount += partialResult.ReusedRemnantsCount;
            mainResult.ReusedArea += partialResult.ReusedArea;
            mainResult.TotalInsulationArea += partialResult.TotalInsulationArea;
            mainResult.WasteArea += partialResult.WasteArea;
            mainResult.WasteFromNewTilesArea += partialResult.WasteFromNewTilesArea;
            mainResult.WasteFromRemnantsArea += partialResult.WasteFromRemnantsArea;
            mainResult.FacadeGrossArea += partialResult.FacadeGrossArea;
            mainResult.WindowsArea += partialResult.WindowsArea;
            mainResult.WindowsCount += partialResult.WindowsCount;
            mainResult.TotalNewMaterialArea += partialResult.TotalNewMaterialArea;
            mainResult.CreatedRemnantsArea += partialResult.CreatedRemnantsArea;
            mainResult.AllTiles.AddRange(partialResult.AllTiles);
            mainResult.HatchRegions.AddRange(partialResult.HatchRegions);
            mainResult.CreatedRemnants.AddRange(partialResult.CreatedRemnants);
            mainResult.Warnings.AddRange(partialResult.Warnings);
            mainResult.Errors.AddRange(partialResult.Errors);

            // [FIX] Propagate OR-Tools diagnostic fields (last facade wins for per-facade data)
            if (partialResult.DiagInput != null)
                mainResult.DiagInput = partialResult.DiagInput;
            if (partialResult.DiagSolution != null)
                mainResult.DiagSolution = partialResult.DiagSolution;

            if (!string.IsNullOrEmpty(partialResult.SolverStatus))
                mainResult.SolverStatus = partialResult.SolverStatus;
            if (!string.IsNullOrEmpty(partialResult.OptimizationMode) && partialResult.OptimizationMode != "Greedy")
                mainResult.OptimizationMode = partialResult.OptimizationMode;

            mainResult.SolveTimeMs += partialResult.SolveTimeMs;

            if (partialResult.OverConsumptionExceeded)
                mainResult.OverConsumptionExceeded = true;
        }

        /// <summary>
        /// Обработка отходов и сохранение остатков
        /// </summary>
        private void ProcessWasteAndSaveRemnants(ProcessingContext context, LayoutResult result)
        {
            // BUG-7 fix: Исключаем остатки "Создан и переиспользован" — они уже были
            // утилизированы внутри RollingHorizon и не должны попадать в БД.
            var validRemnants = result.CreatedRemnants
                .DistinctBy(r => r.Id)
                .Where(r =>
                r.Source != "Создан и переиспользован" &&
                Math.Min(r.Width, r.Height) >= 150 &&
                ((r.Width >= _parameters.MinRemnantWidth && r.Height >= _parameters.MinRemnantWidth) ||
                (r.Width >= _parameters.MinOpeningRemnantWidth && r.Height >= _parameters.MinOpeningRemnantWidth))
            ).ToList();

            // C6: Проставляем судьбу каждого созданного куска
            var validIds = new HashSet<Guid>(validRemnants.Select(r => r.Id));
            foreach (var r in result.CreatedRemnants)
            {
                if (r.Source == "Создан и переиспользован")
                    r.Fate = PieceFate.InWall;
                else if (validIds.Contains(r.Id))
                    r.Fate = PieceFate.InStock;
                else
                    r.Fate = PieceFate.Waste;
            }

            // A1 fix: атомарно помечаем использованные остатки как IsUsed и добавляем новые
            RemnantDatabase.UpdateAfterLayout(context.UsedRemnantIds, validRemnants);

            int wasteCount = result.CreatedRemnants.Count(r => r.Fate == PieceFate.Waste);
            result.Warnings.Add($"Склад обновлён: использовано={context.UsedRemnantIds.Count}, сохранено={validRemnants.Count}, отходы={wasteCount}");
        }

        /// <summary>
        /// Финализация статистики
        /// </summary>
        private void FinalizeStatistics(LayoutResult result)
        {
            // Дополнительная статистика и проверка
            double totalArea = result.NewTilesArea + result.ReusedArea;
            if (totalArea > 0)
            {
                result.Warnings.Add($"Коэффициент использования материала: {result.UtilizationRatio:P1}");
            }

            if (result.CreatedRemnants.Count > 0)
            {
                double totalRemnantArea = result.CreatedRemnants.Sum(r => r.Area);
                result.Warnings.Add($"Общая площадь остатков: {totalRemnantArea / 1000000:F2} м²");
            }
        }

        /// <summary>
        /// Визуализация результатов в AutoCAD (включая маркировку выпусков)
        /// </summary>
        public List<TileVisualization> CreateVisualizationObjects(LayoutResult result)
        {
            var visualizations = new List<TileVisualization>();

            foreach (var tile in result.AllTiles)
            {
                if (string.IsNullOrEmpty(tile.BlockNumber))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[WARN] BlockNumber пуст для плиты Row={tile.RowIndex} W={tile.Width} H={tile.Height} — маркировка на чертеже будет неполной");
                }

                // Аннотация: строка 1 = размеры (например "1200x600"), строка 2 = номер блока (например "1.3.005")
                string annotation = VisualizationEngine.FormatAnnotation(
                    tile.Width, tile.Height, tile.DisplayNumber);

                var viz = VisualizationEngine.CreateTileVisualization(
                    tile.Geometry,
                    tile.IsReused,
                    annotation,
                    tile.HasRelease
                );
                viz.BlockNumber = tile.BlockNumber;
                visualizations.Add(viz);
            }

            return visualizations;
        }
    }
}
