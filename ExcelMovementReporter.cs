using System;
using System.Collections.Generic;
using System.Linq;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.Data
{
    /// <summary>
    /// Обёртка для генерации Excel-отчёта движения остатков.
    /// Вызывается после завершения раскладки из PluginCommands.
    /// </summary>
    public static class ExcelMovementReporter
    {
        /// <summary>
        /// Генерирует Excel-отчёт с полной трассировкой движения остатков.
        /// </summary>
        public static void GenerateMovementReport(
            List<Remnant> initialStock,
            List<Guid> usedRemnantIds,
            List<Remnant> createdRemnants,
            List<Remnant> allRemnants,
            LayoutResult result)
        {
            // Строим DTO статистики из LayoutResult
            var stats = new LayoutStatisticsDTO
            {
                SolverStatus = result.SolverStatus,
                SolveTimeMs = result.SolveTimeMs,
                NewTilesArea = result.NewTilesArea,
                NewTilesCount = result.NewTilesCount,
                ReusedArea = result.ReusedArea,
                ReusedRemnantsCount = result.ReusedRemnantsCount,
                TotalInsulationArea = result.TotalInsulationArea,
                WasteFromNewTilesArea = result.WasteFromNewTilesArea,
                WasteFromRemnantsArea = result.WasteFromRemnantsArea,
                WasteArea = result.WasteArea,
                UtilizationRatio = result.UtilizationRatio,

                FacadeGrossArea = result.FacadeGrossArea,
                WindowsArea = result.WindowsArea,
                WindowsCount = result.WindowsCount,
                NetInsulationArea = result.NetInsulationArea,
                TotalNewMaterialArea = result.TotalNewMaterialArea,
                CreatedRemnantsArea = result.CreatedRemnantsArea,
                OverconsumptionPct = result.OverconsumptionPct,
                OverConsumptionExceeded = result.OverConsumptionExceeded,
                CuttingEfficiencyPct = result.CuttingEfficiencyPct,
                StockSavingsPct = result.StockSavingsPct,

                WasteDisposalCount = createdRemnants.Count(r => r.Fate == PieceFate.Waste),
                WasteDisposalAreaM2 = createdRemnants.Where(r => r.Fate == PieceFate.Waste).Sum(r => r.Area) / 1_000_000.0,
                CreatedRemnantsCount = createdRemnants.DistinctBy(r => r.Id).Count(),
                CreatedRemnantsToStockCount = createdRemnants.DistinctBy(r => r.Id)
                    .Count(r => r.Source != "Создан и переиспользован" && r.Fate != PieceFate.Waste),
                CreatedRemnantsReusedCount = createdRemnants.DistinctBy(r => r.Id)
                    .Count(r => r.Source == "Создан и переиспользован")
            };

            // Диагностика из предупреждений
            var diagnostics = result.Warnings
                .Where(w => w.StartsWith("E_") || w.Contains("ошибка") || w.Contains("Ошибка"))
                .ToList();

            // НЕ глотаем исключение — пробрасываем в PluginCommands для показа пользователю
            RemnantExcelExporter.UpdateWithMovement(
                initialStock,
                usedRemnantIds,
                createdRemnants,
                allRemnants,
                stats,
                diagnostics.Any() ? diagnostics : null,
                result.AllTiles);
        }

        /// <summary>
        /// Вспомогательный метод для создания отчёта напрямую из данных раскладки.
        /// </summary>
        public static void GenerateFromLayoutResult(LayoutResult result, List<Remnant> initialStock)
        {
            var usedIds = result.AllTiles
                .Where(t => t.IsReused && t.SourceRemnantId.HasValue)
                .Select(t => t.SourceRemnantId!.Value)
                .Distinct()
                .ToList();

            var allRemnants = new List<Remnant>(initialStock);
            allRemnants.AddRange(result.CreatedRemnants);

            // НЕ глотаем исключение — пробрасываем в PluginCommands для показа пользователю
            GenerateMovementReport(initialStock, usedIds, result.CreatedRemnants, allRemnants, result);
        }
    }
}
