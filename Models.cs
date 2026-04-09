using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace InsulationMasterPro.Models
{
    public class Remnant
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public double Width { get; set; }
        public double Height { get; set; }
        public double Area => Width * Height;
        public DateTime AddedDate { get; set; } = DateTime.Now;
        public bool IsUsed { get; set; } = false;
        public string Source { get; set; } = "Unknown"; // Источник остатка
        /// <summary>Короткий 4-символьный ID для отображения (из Guid).</summary>
        public string ShortId => Id.ToString("N").Substring(0, 4).ToUpper();
        /// <summary>ID исходного остатка/плиты, из которого был получен данный остаток.</summary>
        public Guid? ParentRemnantId { get; set; }
        /// <summary>Базовый номер блока (например 1.3.005), от которого произошёл этот остаток.</summary>
        public string BaseBlockNumber { get; set; } = string.Empty;
        /// <summary>Счётчик суффиксов (для генерации .01, .02).</summary>
        public int SuffixCounter { get; set; } = 0;
        /// <summary>1-based индекс фасада, на котором создан этот остаток.</summary>
        public int FacadeIndex { get; set; } = 0;
        /// <summary>Индекс ряда (0-based), в котором создан этот остаток. -1 = из начального склада.</summary>
        public int CreatedAtRow { get; set; } = -1;
        /// <summary>История нарезки, например "600→210+390→210+180(отход)".</summary>
        public string CutHistory { get; set; } = string.Empty;
        public Guid? SourceBlockId { get; set; }
        /// <summary>Судьба этого куска: в стене, на складе или отход.</summary>
        public PieceFate Fate { get; set; } = PieceFate.Unknown;
    }

    public class TileInfo
    {
        public Polyline Geometry { get; set; }
        public bool IsReused { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Area
        {
            get
            {
                try { return Geometry?.Area ?? 0; }
                catch (InvalidProgramException) { return Width * Height; }
            }
        }
        public Guid? SourceRemnantId { get; set; }
        /// <summary>Плита с выпуском за контур (первая в ряду с выпуском).</summary>
        public bool HasRelease { get; set; }
        /// <summary>Размер для аннотации: ширина × высота (мм).</summary>
        public string Dimensions => $"{Width:F0}x{Height:F0}";
        public Point3d Position
        {
            get
            {
                try { return Geometry?.GeometricExtents.MinPoint ?? Point3d.Origin; }
                catch (InvalidProgramException) { return Point3d.Origin; }
            }
        }
        /// <summary>Индекс ряда (0-based) в котором размещена плитка.</summary>
        public int RowIndex { get; set; }
        /// <summary>Индекс сегмента в ряду.</summary>
        public int SegmentIndex { get; set; }
        /// <summary>Номер блока в формате X.Y.NNN (новая) или X.Y.NNN.ZZ (остаток). Пример: 1.3.005, 1.3.005.01.</summary>
        public string BlockNumber { get; set; } = string.Empty;
        /// <summary>Номер блока для аннотации на чертеже (совпадает с BlockNumber).</summary>
        public string DisplayNumber => BlockNumber;
        /// <summary>Откуда пришёл блок (для трассировки). Например "Из новой плиты #3" или "Остаток R-0003".</summary>
        public string ParentInfo { get; set; } = string.Empty;
        /// <summary>Размер исходного остатка до размещения (для reused плит). Ширина мм.</summary>
        public double SourceRemnantWidth { get; set; }
        /// <summary>Размер исходного остатка до размещения (для reused плит). Высота мм.</summary>
        public double SourceRemnantHeight { get; set; }
        /// <summary>True если блок является частью IntraRowConsolidate BinPack-цепочки (виртуальный раскрой внутри ряда).</summary>
        public bool IsIntraRowConsolidated { get; set; }
    }

    public class FacadeInfo
    {
        public int Index { get; set; }
        public Polyline Geometry { get; set; }
        public double Width { get { var e = Geometry.GeometricExtents; return e.MaxPoint.X - e.MinPoint.X; } }
        public double Height { get { var e = Geometry.GeometricExtents; return e.MaxPoint.Y - e.MinPoint.Y; } }
        public double MinX { get { var e = Geometry.GeometricExtents; return e.MinPoint.X; } }
        public double MaxX { get { var e = Geometry.GeometricExtents; return e.MaxPoint.X; } }
        public double MinY { get { var e = Geometry.GeometricExtents; return e.MinPoint.Y; } }
        public double MaxY { get { var e = Geometry.GeometricExtents; return e.MaxPoint.Y; } }
        public double CenterX => (MinX + MaxX) / 2;
        public double CenterY => (MinY + MaxY) / 2;
    }

    public class LayoutResult
    {
        public int NewTilesCount { get; set; }
        public double NewTilesArea { get; set; }
        public int ReusedRemnantsCount { get; set; }
        public double ReusedArea { get; set; }
        public double TotalInsulationArea { get; set; }
        public List<Remnant> CreatedRemnants { get; set; } = new List<Remnant>();
        public List<TileInfo> AllTiles { get; set; } = new List<TileInfo>();
        /// <summary>Области штриховки: перекрытия блоков с оконными проемами в верхнем/нижнем рядах (ТЗ п.2.7).</summary>
        public List<Polyline> HatchRegions { get; set; } = new List<Polyline>();
        /// <summary>Список фасадов для визуальной маркировки (ТЗ п.5).</summary>
        public List<FacadeInfo> FacadeInfos { get; set; } = new List<FacadeInfo>();
        public double WasteArea { get; set; }
        /// <summary>Отходы (м²), образовавшиеся при раскрое целых плит (фрагменты с длиной &lt; 150 мм).</summary>
        public double WasteFromNewTilesArea { get; set; }
        /// <summary>Отходы (м²), образовавшиеся при раскрое остатков со склада (фрагменты с длиной &lt; 150 мм).</summary>
        public double WasteFromRemnantsArea { get; set; }
        public double UtilizationRatio => TotalInsulationArea > 0 ? (TotalInsulationArea / (TotalInsulationArea + WasteArea)) : 0;
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        
        // OR-Tools specific fields
        /// <summary>Режим оптимизации (Greedy или OR-Tools).</summary>
        public string OptimizationMode { get; set; } = "Greedy";
        /// <summary>Время решения в миллисекундах (для OR-Tools).</summary>
        public long SolveTimeMs { get; set; }
        /// <summary>Статус решателя (для OR-Tools).</summary>
        public string SolverStatus { get; set; } = string.Empty;

        // Материальный баланс
        /// <summary>Площадь фасада брутто (м²).</summary>
        public double FacadeGrossArea { get; set; }
        /// <summary>Площадь оконных проёмов (м²).</summary>
        public double WindowsArea { get; set; }
        /// <summary>Количество оконных проёмов.</summary>
        public int WindowsCount { get; set; }
        /// <summary>Чистая площадь утепления = база 100% (м²).</summary>
        public double NetInsulationArea => FacadeGrossArea - WindowsArea;
        /// <summary>Общая площадь нового материала = NewTilesCount × TileArea (м²).</summary>
        public double TotalNewMaterialArea { get; set; }
        /// <summary>Площадь остатков, отправленных на склад (м²).</summary>
        public double CreatedRemnantsArea { get; set; }
        /// <summary>Перерасход нового материала (%).</summary>
        public double OverconsumptionPct => NetInsulationArea > 0
            ? (TotalNewMaterialArea / NetInsulationArea * 100.0 - 100.0) : 0;
        /// <summary>Превышен ли лимит перерасхода 7%.</summary>
        public bool OverConsumptionExceeded { get; set; }
        /// <summary>КПД раскроя (%).</summary>
        public double CuttingEfficiencyPct => (TotalNewMaterialArea + ReusedArea) > 0
            ? (NetInsulationArea / (TotalNewMaterialArea + ReusedArea) * 100.0) : 0;
        /// <summary>Экономия от остатков (%).</summary>
        public double StockSavingsPct => NetInsulationArea > 0
            ? (ReusedArea / NetInsulationArea * 100.0) : 0;
        /// <summary>Процент отходов от общего нового материала (%).</summary>
        public double WastePct => TotalNewMaterialArea > 0
            ? (WasteArea / TotalNewMaterialArea * 100.0) : 0;

        // Diagnostic data — populated by LayoutEngine for DiagnosticLogger
        /// <summary>Raw FacadeInput for diagnostic dump (не сериализуется).</summary>
        [Newtonsoft.Json.JsonIgnore]
        public OrTools.FacadeInput DiagInput { get; set; }
        /// <summary>Raw solver solution for diagnostic dump (не сериализуется).</summary>
        [Newtonsoft.Json.JsonIgnore]
        public OrTools.OrToolsSolution DiagSolution { get; set; }
    }

    public class ProcessingContext
    {
        public List<Polyline> Facades { get; set; } = new List<Polyline>();
        public List<Polyline> Windows { get; set; } = new List<Polyline>();
        public List<Remnant> AvailableRemnants { get; set; } = new List<Remnant>();
        /// <summary>ID остатков со склада, фактически использованных в раскладке (для пометки IsUsed в БД).</summary>
        public HashSet<Guid> UsedRemnantIds { get; set; } = new HashSet<Guid>();
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public DateTime ProcessingStartTime { get; set; } = DateTime.Now;
        public TimeSpan ProcessingTime => DateTime.Now - ProcessingStartTime;
    }

    /// <summary>Судьба каждого куска материала (ideal_logic §Итог).</summary>
    public enum PieceFate
    {
        /// <summary>Не определена (ещё в процессе).</summary>
        Unknown,
        /// <summary>Кусок уложен на фасад — уже в стене.</summary>
        InWall,
        /// <summary>Кусок сохранён на складе — пригоден для следующего фасада.</summary>
        InStock,
        /// <summary>Кусок меньше минимума — отход, выброшен.</summary>
        Waste
    }

    public enum TileOrientation
    {
        Horizontal,  // 1200 × 600
        Vertical     // 600 × 1200
    }

    public class TileSpecification
    {
        public double Width { get; set; } = 1200.0;
        public double Height { get; set; } = 600.0;
        public double Thickness { get; set; } = 150.0;
        public TileOrientation DefaultOrientation { get; set; } = TileOrientation.Horizontal;
        public bool AllowRotation { get; set; } = true;
    }

    public class LayoutParameters
    {
        public double MinRemnantWidth { get; set; } = 200.0;
        public double MinOpeningRemnantWidth { get; set; } = 150.0;
        /// <summary>Минимальная ширина перекрытия стыка рядов (мм).</summary>
        public double MinJointOffset { get; set; } = 100.0;
        /// <summary>Максимальная ширина перекрытия стыка рядов (мм).</summary>
        public double MaxJointOffset { get; set; } = 400.0;
        public double WindowOverlap { get; set; } = 20.0;
        public double OptimizationStep { get; set; } = 10.0; // ТЗ п.3.3: шаг перебора 10 мм
        public int MaxOptimizationIterations { get; set; } = 20;
        public bool CreateLBoots { get; set; } = true;
        public bool OptimizeRemnants { get; set; } = true;
        /// <summary>Выпуск за контур: начинается с 1-го ряда (true) или со 2-го (false).</summary>
        public bool FirstRowWithRelease { get; set; } = true;
        /// <summary>Величина выпуска за контур (мм).</summary>
        public double ReleaseOverhang { get; set; } = 170.0; // ТЗ п.2.2: выпуск за контур 170 мм
        /// <summary>Псевдоним для ReleaseOverhang для совместимости с LayoutEngine.</summary>
        public double ReleaseAmount => ReleaseOverhang;
        
        // OR-Tools configuration
        /// <summary>Использовать OR-Tools для глобальной оптимизации.</summary>
        public bool UseOrTools { get; set; } = false;
        /// <summary>Таймаут для OR-Tools решателя (секунды).</summary>
        public int OrToolsTimeoutSeconds { get; set; } = 60;
        /// <summary>Использовать скользящее окно (2–3 ряда за раз) — проще и устойчивее; при false — полная глобальная оптимизация.</summary>
        public bool UseRollingHorizon { get; set; } = true;
        /// <summary>Размер окна при скользящем горизонте. 1 — каждый ряд независимо (максимальная скорость, безопасный старт). 2–3 — look-ahead; требует снизить RollingHorizonTimeoutPerWindowSeconds иначе время ×N.</summary>
        public int RollingHorizonRows { get; set; } = 1;
        /// <summary>Таймаут на одно окно (секунды) при скользящем горизонте.</summary>
        public int RollingHorizonTimeoutPerWindowSeconds { get; set; } = 5;
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<Polyline> ValidContours { get; set; } = new List<Polyline>();
    }

    /// <summary>
    /// DTO для передачи статистики раскладки в Excel-отчёт.
    /// </summary>
    public class LayoutStatisticsDTO
    {
        public string SolverStatus { get; set; } = "";
        public long SolveTimeMs { get; set; }
        public double NewTilesArea { get; set; }
        public int NewTilesCount { get; set; }
        public double ReusedArea { get; set; }
        public int ReusedRemnantsCount { get; set; }
        public double TotalInsulationArea { get; set; }
        public double WasteFromNewTilesArea { get; set; }
        public double WasteFromRemnantsArea { get; set; }
        public double WasteArea { get; set; }
        public double UtilizationRatio { get; set; }

        // Материальный баланс
        public double FacadeGrossArea { get; set; }
        public double WindowsArea { get; set; }
        public int WindowsCount { get; set; }
        public double NetInsulationArea { get; set; }
        public double TotalNewMaterialArea { get; set; }
        public double CreatedRemnantsArea { get; set; }
        public double OverconsumptionPct { get; set; }
        public bool OverConsumptionExceeded { get; set; }
        public double CuttingEfficiencyPct { get; set; }
        public double StockSavingsPct { get; set; }

        // Отходы на выброс (PieceFate.Waste)
        public int WasteDisposalCount { get; set; }
        public double WasteDisposalAreaM2 { get; set; }
        public int CreatedRemnantsCount { get; set; }
        public int CreatedRemnantsToStockCount { get; set; }
        public int CreatedRemnantsReusedCount { get; set; }
    }
}
