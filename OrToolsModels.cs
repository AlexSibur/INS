using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.OrTools
{
    /// <summary>
    /// Определение ряда фасада для OR-Tools
    /// </summary>
    public class RowDefinition
    {
        public int RowIndex { get; set; }
        public double Y { get; set; }
        public double Height { get; set; }
        public bool HasRelease { get; set; }
        /// <summary>Тип ряда относительно окон (ТЗ v13.1 §8).</summary>
        public RowType RowType { get; set; } = RowType.Normal;
        public List<RowSegment> Segments { get; set; } = new List<RowSegment>();
    }

    /// <summary>
    /// Сегмент ряда (между окнами или границами фасада)
    /// </summary>
    public class RowSegment
    {
        public int RowIndex { get; set; }
        public int SegmentIndex { get; set; }
        public double StartX { get; set; }
        public double EndX { get; set; }
        public double Width => EndX - StartX;
        public bool NearWindowLeft { get; set; }
        public bool NearWindowRight { get; set; }
        public bool IsReleaseLeft { get; set; }   // Выпуск слева (первый сегмент в ряду с выпуском)
        public bool IsReleaseRight { get; set; }  // Выпуск справа (последний сегмент в ряду с выпуском)
    }

    /// <summary>
    /// Информация об окне
    /// </summary>
    public class WindowInfo
    {
        public string Id { get; set; } = string.Empty;
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        
        public static WindowInfo FromPolyline(Polyline poly, int index)
        {
            var ext = poly.GeometricExtents;
            return new WindowInfo
            {
                Id = $"w{index}",
                MinX = ext.MinPoint.X,
                MinY = ext.MinPoint.Y,
                MaxX = ext.MaxPoint.X,
                MaxY = ext.MaxPoint.Y
            };
        }
    }

    /// <summary>
    /// Блок плитки в паттерне
    /// </summary>
    public class Block
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public double X { get; set; }
        public double Width { get; set; }
        public BlockType Type { get; set; }
        public Guid? RemnantId { get; set; }
        public double CutFrom { get; set; }  // Исходный размер (если раскрой остатка)
        public bool IsRotated { get; set; }  // Повёрнут на 90°
    }

    public enum BlockType
    {
        New,       // Новая плита
        Remnant,   // Остаток со склада
        CutRemnant // Раскроенный остаток
    }

    /// <summary>
    /// Тип ряда относительно оконного проёма (ТЗ v13.1 §8, §29.1).
    /// </summary>
    public enum RowType
    {
        /// <summary>Ряд не пересекает ни одно окно.</summary>
        Normal,
        /// <summary>Нижняя граница окна попадает в этот ряд → Г-элемент снизу.</summary>
        BottomEdge,
        /// <summary>Ряд полностью внутри окна → зона проёма пуста, только боковины.</summary>
        Middle,
        /// <summary>Верхняя граница окна попадает в этот ряд → Г-элемент сверху.</summary>
        TopEdge
    }

    /// <summary>
    /// Г-образный элемент, сформированный вырезкой из одного блока (ТЗ v13.1 §14, C16a).
    /// </summary>
    public class LBootElement
    {
        /// <summary>ID окна, для которого создан элемент.</summary>
        public string WindowId { get; set; } = string.Empty;
        /// <summary>Угол: BL (нижний-левый), BR, TL, TR.</summary>
        public string Corner { get; set; } = string.Empty;
        /// <summary>Индекс ряда, в котором находится элемент.</summary>
        public int RowIndex { get; set; }
        /// <summary>X-координата блока-источника.</summary>
        public double SourceBlockX { get; set; }
        /// <summary>Ширина исходного блока до вырезки.</summary>
        public double SourceBlockWidth { get; set; }
        /// <summary>Ширина горизонтальной полки (мм). Должна быть ≥ 150.</summary>
        public double ShelfH { get; set; }
        /// <summary>Ширина вертикальной полки (мм). Должна быть ≥ 150.</summary>
        public double ShelfV { get; set; }
        /// <summary>Ширина вырезанного прямоугольника.</summary>
        public double CutoutWidth { get; set; }
        /// <summary>Высота вырезанного прямоугольника.</summary>
        public double CutoutHeight { get; set; }
    }

    /// <summary>
    /// Паттерн раскладки блоков для одного сегмента
    /// </summary>
    public class BlockPattern
    {
        public int PatternId { get; set; }
        public int RowIndex { get; set; }
        public int SegmentIndex { get; set; }
        public List<Block> Blocks { get; set; } = new List<Block>();
        public double TotalWidth => Blocks.Sum(b => b.Width);
        public HashSet<Guid> UsedRemnantIds { get; set; } = new HashSet<Guid>();
        public List<double> JointPositions { get; set; } = new List<double>();
        
        /// <summary>
        /// Площадь использованных остатков (для целевой функции)
        /// </summary>
        public double UsedRemnantArea { get; set; }
        
        /// <summary>
        /// Площадь новых остатков, создаваемых при раскладке
        /// </summary>
        public double CreatedRemnantArea { get; set; }

        /// <summary>
        /// Количество новых плит (целых + обрезанных), используемых в паттерне
        /// </summary>
        public int NewTileCount { get; set; }
    }

    /// <summary>
    /// Ограничения оптимизации
    /// </summary>
    public class OptimizationConstraints
    {
        public double MinBlock { get; set; } = 200.0;
        public double MinBlockNearWindow { get; set; } = 150.0;
        public int MaxConsecutiveRemnants { get; set; } = 2;
        public int MaxConsecutiveRemnantsWindow { get; set; } = 3;
        public double MinJointOffset { get; set; } = 100.0;
        public double ReleaseOverhang { get; set; } = 170.0;
        public double MinReleasePiece { get; set; } = 370.0;
        public double MinRowHeight { get; set; } = 150.0;
        public double MinRemnantToSave { get; set; } = 150.0;
        public double TileWidth { get; set; } = 1200.0;
        public double TileHeight { get; set; } = 600.0;
        public double WindowOverlap { get; set; } = 20.0;
        public double CornerZone { get; set; } = 150.0;
    }

    /// <summary>
    /// Входные данные для OR-Tools оптимизатора
    /// </summary>
    public class FacadeInput
    {
        public string FacadeId { get; set; } = string.Empty;
        /// <summary>1-based index of the facade for tile numbering (X.Y.NNN.ZZ format).</summary>
        public int FacadeIndex { get; set; } = 1;
        public double Width { get; set; }
        public double Height { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double TrueFacadeAreaM2 { get; set; }
        public double TrueWindowsAreaM2 { get; set; }
        public List<RowDefinition> Rows { get; set; } = new List<RowDefinition>();
        public List<WindowInfo> Windows { get; set; } = new List<WindowInfo>();
        public List<Remnant> StockRemnants { get; set; } = new List<Remnant>();
        public OptimizationConstraints Constraints { get; set; } = new OptimizationConstraints();
        public bool FirstRowWithRelease { get; set; } = true;
        
        /// <summary>Синхронизированные высоты рядов из первого фасада (для Row Sync ТЗ п.3).</summary>
        public List<double>? SyncedRowHeights { get; set; }

        /// <summary>
        /// Ширины сегментов будущих рядов (за пределами текущего окна) — для look-ahead оптимизации.
        /// Паттерны, создающие остатки подходящие под эти ширины, получают бонус.
        /// </summary>
        public List<double> FutureSegmentWidths { get; set; } = new List<double>();
        
        /// <summary>
        /// Все сегменты всех рядов (плоский список для удобства)
        /// </summary>
        public List<RowSegment> AllSegments => Rows.SelectMany(r => r.Segments).ToList();

        /// <summary>
        /// Подсказки глобальной аллокации: GUID остатка → индекс ряда, для которого остаток лучше всего подходит.
        /// Заполняется PreAllocateRemnants в RollingHorizonEngine (если EnableRemnantPreAllocation=true).
        /// Используется в OrToolsOptimizer.BuildObjective для soft-penalizing "чужих" рядов.
        /// </summary>
        public Dictionary<Guid, int> RemnantRowHints { get; set; } = new Dictionary<Guid, int>();
    }

    /// <summary>
    /// Результат раскладки одного ряда
    /// </summary>
    public class RowSolution
    {
        public int RowIndex { get; set; }
        public double Height { get; set; }
        public double Y { get; set; }
        public List<SegmentSolution> Segments { get; set; } = new List<SegmentSolution>();

        /// <summary>
        /// Возвращает X-координаты стыков (швов) между блоками в ряду — для ограничения перевязки со следующим рядом.
        /// </summary>
        public static List<double> GetJointPositions(RowSolution row)
        {
            const double tol = 0.001;
            var joints = new List<double>();
            if (row?.Segments == null) return joints;
            foreach (var seg in row.Segments)
            {
                double x = seg.StartX;
                foreach (var block in seg.Blocks)
                {
                    x += block.Width;
                    if (x < seg.EndX - tol)
                        joints.Add(x);
                }
            }
            return joints;
        }
    }

    /// <summary>
    /// Результат раскладки одного сегмента
    /// </summary>
    public class SegmentSolution
    {
        public int SegmentIndex { get; set; }
        public double StartX { get; set; }
        public double EndX { get; set; }
        public List<Block> Blocks { get; set; } = new List<Block>();
    }

    /// <summary>
    /// Остаток после раскладки
    /// </summary>
    public class RemnantAfter
    {
        public Guid Id { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public RemnantStatus Status { get; set; }
        public Guid? SourceBlockId { get; set; }
    }

    public enum RemnantStatus
    {
        Unused,              // Не использован (остался на складе)
        CreatedFromNew,      // Создан при раскрое новой плиты
        CreatedFromCut,      // Создан при раскрое существующего остатка
        CreatedAndReused     // Создан при раскрое и переиспользован в последующем ряду
    }

    /// <summary>
    /// Решение OR-Tools оптимизатора
    /// </summary>
    public class OrToolsSolution
    {
        public string FacadeId { get; set; } = string.Empty;
        public List<RowSolution> Rows { get; set; } = new List<RowSolution>();
        public List<RemnantAfter> StockAfter { get; set; } = new List<RemnantAfter>();
        public double TotalRemainingArea { get; set; }
        public SolverStatus Status { get; set; }
        public long SolveTimeMs { get; set; }
        public double ObjectiveValue { get; set; }
        /// <summary>Диагностика при неудаче: для какого ряда/сегмента и в чём именно не найдено решение.</summary>
        public List<string> Diagnostics { get; set; } = new List<string>();
        /// <summary>Г-образные элементы, сформированные при обработке окон (ТЗ v13.1 §14, §29).</summary>
        public List<LBootElement> LBootElements { get; set; } = new List<LBootElement>();
        /// <summary>Остатки, созданные во время скользящего окна (для корректного маппинга номеров Nxxxx.x).</summary>
        public List<Remnant> IntraFacadeRemnants { get; set; } = new List<Remnant>();
    }

    public enum SolverStatus
    {
        Optimal,
        Feasible,
        Infeasible,
        Timeout,
        Error
    }

    /// <summary>
    /// Конфигурация оптимизатора
    /// </summary>
    public class OptimizerConfig
    {
        public int TimeoutSeconds { get; set; } = 60;
        public int MaxPatternsPerSegment { get; set; } = 2_500;
        public bool LogProgress { get; set; } = true;
        public int OffsetStep { get; set; } = 10;  // Шаг смещения (мм)

        /// <summary>Использовать скользящее окно (2–3 ряда за раз, коммит первого). Упрощает модель и уменьшает таймауты.</summary>
        public bool UseRollingHorizon { get; set; } = true;

        /// <summary>
        /// Размер окна при UseRollingHorizon: сколько рядов решаем за один вызов.
        /// 1 — каждый ряд независимо (максимальная скорость, дефолт).
        /// 2..3 — sliding window look-ahead; требует снизить RollingHorizonTimeoutPerWindowSeconds
        ///        иначе время оптимизации умножается на HorizonRows.
        /// ВАЖНО: значение 3 было до реализации sliding window — тогда оно игнорировалось.
        /// Теперь оно реально используется; безопасный старт = 1.
        /// </summary>
        public int RollingHorizonRows { get; set; } = 1;

        /// <summary>Таймаут на одно окно в секундах (при Rolling Horizon).</summary>
        public int RollingHorizonTimeoutPerWindowSeconds { get; set; } = 5;

        /// <summary>Вес количества новых плит в целевой функции.
        /// Баланс: достаточно высокий чтобы предпочитать паттерны с меньшим числом плит
        /// при прочих равных, но не настолько большой чтобы подавить утилизацию остатков.</summary>
        public int TileWeight { get; set; } = 10_000;

        /// <summary>
        /// Вес использования остатков в целевой функции (множитель площади).
        /// Безопасный диапазон при TileWeight=10000, SCALE=100: RemnantUsageWeight ≤ 137
        /// (иначе бонус за 1м² остатка превысит стоимость открытия новой плиты).
        /// RollingHorizonEngine переопределяет: pass1=100, pass2=120.
        /// </summary>
        public int RemnantUsageWeight { get; set; } = 100;

        /// <summary>Макс. количество остатков со склада, передаваемых солверу (без учёта VirtualCutout).
        /// FIX: увеличен с 50 до 80 для лучшей утилизации на мультифасадных проектах (склад 100–300+).</summary>
        public int MaxStockForSolver { get; set; } = 80;

        /// <summary>
        /// Экспериментальная row-level целевая функция.
        /// При true — вместо Σ(NewTileCount_сегмента × TileWeight) солвер использует
        /// ceil(Σ(NewWidth_всех_сегментов_ряда) / TileWidth) × TileWeight.
        /// Корректно моделирует физические плиты на этапе решения.
        /// На практике IntraRowConsolidate уже решает проблему пост-фактум,
        /// поэтому row-level objective не даёт дополнительного прироста.
        /// Оставлен как опция для будущих экспериментов. По умолчанию выключен.
        /// </summary>
        public bool EnableRowLevelObjective { get; set; } = false;

        /// <summary>
        /// Вес использования остатков для обоих проходов Rolling Horizon (pass1=MaxRemnantUsageWeight-20, pass2=MaxRemnantUsageWeight).
        /// Безопасный максимум при TileWeight=10000, SCALE=100: ≤ 137.
        /// Значение 135 обеспечивает сильный сигнал приоритета остатков с запасом.
        /// </summary>
        public int MaxRemnantUsageWeight { get; set; } = 135;

        /// <summary>
        /// Минимальный размер стороны остатка (мм) для применения бонуса Improvement C.
        /// Остатки с min(Width, Height) ≥ этого порога получают приоритетный бонус в CP-SAT objective.
        /// Значение по умолчанию 350 мм соответствует типичной высоте краевых рядов.
        /// </summary>
        public double ImprovementCMinDim { get; set; } = 350.0;

        /// <summary>
        /// Применять бонус Improvement C к узким полосам-остаткам (narrow strips),
        /// у которых max(Width, Height) ≥ TileHeight, даже если min(Width, Height) &lt; ImprovementCMinDim.
        /// Пример: 1200×200мм — длинная сторона 1200 ≥ 600 (TileHeight) → бонус применяется.
        /// По умолчанию выключен; включить для очистки склада от накопившихся узких полос.
        /// </summary>
        public bool EnableNarrowStripBonus { get; set; } = false;

        /// <summary>
        /// Стратегия сортировки остатков при подборе.
        /// AreaFirst — классическая (крупные сначала, затем MatchScore). Консервативна, очищает склад.
        /// CombinedScore — взвешенный скор: 50% MatchScore + 30% нормированная площадь + 20% utilization.
        ///   Лучше для минимизации потерь при раскрое остатков.
        /// </summary>
        public RemnantSortStrategy RemnantSortStrategy { get; set; } = RemnantSortStrategy.CombinedScore;

        /// <summary>Вес MatchScore в комбинированном скоре (RemnantSortStrategy.CombinedScore).</summary>
        public double RemnantScoreWeightMatch { get; set; } = 0.5;

        /// <summary>Вес нормированной площади в комбинированном скоре.</summary>
        public double RemnantScoreWeightArea { get; set; } = 0.3;

        /// <summary>Вес utilization в комбинированном скоре.</summary>
        public double RemnantScoreWeightUtilization { get; set; } = 0.2;

        /// <summary>
        /// Включить глобальный greedy pre-pass аллокации остатков по рядам перед Rolling Horizon.
        /// Pre-pass вычисляет RemnantRowHints: GUID→rowIndex.
        /// Остатки, переданные не на "свой" ряд, получают сниженный effectiveArea в CP-SAT (soft hint).
        /// По умолчанию выключен; включить явно для экспериментов.
        /// </summary>
        public bool EnableRemnantPreAllocation { get; set; } = false;

        /// <summary>
        /// Коэффициент снижения effectiveArea для остатков, используемых не на "своём" ряду (0..1).
        /// 0.5 = штраф 50% для "чужого" ряда.
        /// </summary>
        public double RemnantPreAllocationHintPenalty { get; set; } = 0.5;
    }

    /// <summary>
    /// Стратегия сортировки кандидатов в RemnantManager.FindBestRemnant.
    /// </summary>
    public enum RemnantSortStrategy
    {
        /// <summary>Сортировка по убыванию площади остатка (классика). Освобождает склад от крупных кусков первыми.</summary>
        AreaFirst,
        /// <summary>Взвешенный комбинированный скор: MatchScore + нормированная площадь + utilization.</summary>
        CombinedScore
    }
}
