using System;
using System.Collections.Generic;
using System.Linq;
using InsulationMasterPro.Models;
using InsulationMasterPro.OrTools;

namespace InsulationMasterPro.Data
{
    public class RemnantMatch
    {
        public Remnant Remnant { get; set; }
        public double MatchScore { get; set; } // Оценка соответствия (0-1, чем выше тем лучше)
        public double WasteArea { get; set; } // Площадь отхода при использовании
        public bool RequiresRotation { get; set; }
    }

    public static class RemnantManager
    {
        private const double MIN_REMNANT_WIDTH = 200.0;
        private const double MIN_REMNANT_HEIGHT = 150.0; // У окон
        private const double TILE_WIDTH = 1200.0;
        private const double TILE_HEIGHT = 600.0;

        /// <summary>
        /// Находит наиболее подходящий остаток для слота.
        /// slotWidth/slotHeight — размеры доступного пространства (для скоринга).
        /// minWidth — минимальная допустимая ширина остатка по длине ряда;
        ///   если 0, остаток должен покрывать весь слот (≥slotWidth).
        /// sortStrategy — стратегия сортировки кандидатов (CombinedScore по умолчанию).
        /// ТЗ п.3.3: остаток может быть от 200 мм (150 у окна), не обязан закрывать весь слот 1200.
        /// </summary>
        public static RemnantMatch FindBestRemnant(List<Remnant> remnants, double slotWidth, double slotHeight,
            bool nearWindow = false, double minWidth = 0,
            RemnantSortStrategy sortStrategy = RemnantSortStrategy.CombinedScore,
            double wMatch = 0.5, double wArea = 0.3, double wUtil = 0.2,
            bool allowNarrowRotated = false)
        {
            double minLength = nearWindow ? MIN_REMNANT_HEIGHT : MIN_REMNANT_WIDTH;
            // effectiveMinWidth — порог фильтрации остатков:
            //   minWidth > 0 → свободный подбор (остаток от minWidth, слот для скоринга)
            //   minWidth = 0 → строгое соответствие (остаток >= slotWidth, для ProcessOptimizationRow)
            double effectiveMinWidth = minWidth > 0 ? Math.Max(minWidth, minLength) : slotWidth;

            if (slotWidth < minLength)
                return null;

            var candidates = new List<RemnantMatch>();

            foreach (var remnant in remnants)
            {
                var match = EvaluateRemnantMatch(remnant, slotWidth, slotHeight, effectiveMinWidth, allowNarrowRotated);
                if (match != null)
                {
                    candidates.Add(match);
                }
            }

            // ТЗ §16: минимизация остатков на складе. Порог утилизации 8% из spec.
            const double MIN_UTILIZATION = 0.08;
            candidates = candidates
                .Where(c =>
                {
                    double remW = c.RequiresRotation ? c.Remnant.Height : c.Remnant.Width;
                    double remH = c.RequiresRotation ? c.Remnant.Width : c.Remnant.Height;
                    double usedW = Math.Min(remW, slotWidth);
                    double usedH = Math.Min(remH, slotHeight);
                    double utilization = (usedW * usedH) / (remW * remH);
                    bool accepted = utilization >= MIN_UTILIZATION;
                    System.Diagnostics.Debug.WriteLine(
                        $"DEBUG [RemnantManager.FindBestRemnant] " +
                        $"candidate id={c.Remnant.Id} {remW:F0}×{remH:F0} " +
                        $"utilization={utilization * 100:F1}% score={c.MatchScore:F3} " +
                        $"{(accepted ? "ACCEPT" : $"REJECT (<{MIN_UTILIZATION * 100:F0}%)")}");
                    return accepted;
                })
                .ToList();

            if (candidates.Count == 0)
                return null;

            IOrderedEnumerable<RemnantMatch> sorted;

            if (sortStrategy == RemnantSortStrategy.CombinedScore)
            {
                // Комбинированный скор: баланс между MatchScore, площадью и utilitization.
                // wMatch=0.5 гарантирует, что форма важнее размера; wArea=0.3 сохраняет тягу к крупным.
                double maxArea = candidates.Max(c => c.Remnant.Area);
                sorted = candidates
                    .OrderByDescending(c =>
                    {
                        double remW = c.RequiresRotation ? c.Remnant.Height : c.Remnant.Width;
                        double remH = c.RequiresRotation ? c.Remnant.Width : c.Remnant.Height;
                        double usedW = Math.Min(remW, slotWidth);
                        double usedH = Math.Min(remH, slotHeight);
                        double utilization = (usedW * usedH) / (remW * remH);
                        double utilizationBonus = utilization >= 0.7 ? 1.0 : 0.5;
                        double areaNorm = maxArea > 0 ? c.Remnant.Area / maxArea : 0;
                        double combined = wMatch * c.MatchScore + wArea * areaNorm + wUtil * utilizationBonus;
                        System.Diagnostics.Debug.WriteLine(
                            $"DEBUG [RemnantManager] Candidate {c.Remnant.Id}: " +
                            $"matchScore={c.MatchScore:F3} areaNorm={areaNorm:F3} utilBonus={utilizationBonus:F1} " +
                            $"combined={combined:F3}");
                        return combined;
                    })
                    .ThenByDescending(c => c.MatchScore)
                    .ThenBy(c => c.WasteArea);
            }
            else
            {
                // AreaFirst: классическая стратегия — крупные сначала → очистка склада
                sorted = candidates
                    .OrderByDescending(c => c.Remnant.Area)
                    .ThenByDescending(c => c.MatchScore)
                    .ThenBy(c => c.WasteArea);
            }

            var result = sorted.FirstOrDefault();
            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [RemnantManager] Selected remnant {result?.Remnant.Id} from {candidates.Count} candidates, " +
                $"strategy={sortStrategy}");

            return result;
        }

        /// <summary>
        /// Оценивает, подходит ли остаток для данного слота.
        /// effectiveMinWidth — минимальная допустимая ширина остатка по длине ряда (200/150/370 мм).
        /// slotWidth — полная ширина текущего слота (для скоринга, НЕ для фильтрации).
        /// slotHeight — высота ряда (600 мм).
        /// Если обе ориентации feasible — возвращает ту, у которой выше MatchScore.
        /// </summary>
        private static RemnantMatch EvaluateRemnantMatch(Remnant remnant, double slotWidth, double slotHeight,
            double effectiveMinWidth, bool allowNarrowRotated = false)
        {
            bool normalFeasible = remnant.Width >= effectiveMinWidth && remnant.Height >= slotHeight;
            bool rotatedFeasible = remnant.Height >= effectiveMinWidth && remnant.Width >= slotHeight;

            // [NARROW-STRIP] Узкая полоса в ротированной ориентации:
            // длинная сторона >= slotHeight (высота ряда), короткая >= MIN_REMNANT_WIDTH.
            // В full-slot режиме (effectiveMinWidth = slotWidth) такая полоса отклоняется по ширине,
            // хотя физически может быть обрезана по ширине до нужного размера.
            bool narrowRotatedFeasible = allowNarrowRotated
                && remnant.Width >= slotHeight        // длинная сторона покрывает высоту ряда
                && remnant.Height >= MIN_REMNANT_WIDTH; // короткая >= MinBlock

            if (narrowRotatedFeasible && !rotatedFeasible)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[NARROW-STRIP] EvaluateRemnantMatch: remnant {remnant.Id} " +
                    $"({remnant.Width:F0}x{remnant.Height:F0}) allowNarrowRotated → rotated feasible " +
                    $"(effectiveMinWidth={effectiveMinWidth:F0}, slotHeight={slotHeight:F0})");
                rotatedFeasible = true;
            }

            if (!normalFeasible && !rotatedFeasible)
            {
                // Диагностика: полоса с длинной стороной >= slotHeight, но отклонена
                if (remnant.Width >= slotHeight && remnant.Height >= MIN_REMNANT_WIDTH)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[NARROW-STRIP] REJECT: remnant {remnant.Id} " +
                        $"({remnant.Width:F0}x{remnant.Height:F0}) rejected — " +
                        $"rotated width={remnant.Height:F0} < effectiveMinWidth={effectiveMinWidth:F0} " +
                        $"(full-slot mode). Use allowNarrowRotated=true to allow partial coverage.");
                }
                return null;
            }

            if (normalFeasible && !rotatedFeasible)
                return CreateMatch(remnant, slotWidth, slotHeight, false);

            if (!normalFeasible && rotatedFeasible)
                return CreateMatch(remnant, slotWidth, slotHeight, true);

            // Обе ориентации feasible — выбираем лучшую по MatchScore
            var normalMatch = CreateMatch(remnant, slotWidth, slotHeight, false);
            var rotatedMatch = CreateMatch(remnant, slotWidth, slotHeight, true);

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [RemnantManager] Remnant {remnant.Id} ({remnant.Width:F0}×{remnant.Height:F0}): " +
                $"normal score={normalMatch.MatchScore:F3}, rotated score={rotatedMatch.MatchScore:F3}, " +
                $"selected={(rotatedMatch.MatchScore > normalMatch.MatchScore ? "rotated" : "normal")}");

            return rotatedMatch.MatchScore > normalMatch.MatchScore ? rotatedMatch : normalMatch;
        }

        /// <summary>
        /// Создает объект соответствия остатка.
        /// Остаток может быть как меньше, так и больше слота; оба случая корректны.
        /// </summary>
        private static RemnantMatch CreateMatch(Remnant remnant, double slotWidth, double slotHeight, bool rotated)
        {
            double remnantWidth = rotated ? remnant.Height : remnant.Width;
            double remnantHeight = rotated ? remnant.Width : remnant.Height;

            // Фактически используемая площадь (min по каждой оси)
            double usedWidth = Math.Min(remnantWidth, slotWidth);
            double usedHeight = Math.Min(remnantHeight, slotHeight);
            double usedArea = usedWidth * usedHeight;
            double wasteArea = (remnantWidth * remnantHeight) - usedArea;

            double matchScore = CalculateMatchScore(remnantWidth, remnantHeight, slotWidth, slotHeight, usedArea);

            return new RemnantMatch
            {
                Remnant = remnant,
                MatchScore = matchScore,
                WasteArea = wasteArea,
                RequiresRotation = rotated
            };
        }

        /// <summary>
        /// Скоринг: баланс между покрытием слота и использованием остатка.
        /// Предпочтителен остаток, максимально совпадающий с размером слота.
        /// </summary>
        private static double CalculateMatchScore(double remnantWidth, double remnantHeight,
            double slotWidth, double slotHeight, double usedArea)
        {
            double slotArea = slotWidth * slotHeight;
            double remnantArea = remnantWidth * remnantHeight;

            // Покрытие слота: какую долю слота закрывает остаток (0..1)
            double coverageRatio = slotArea > 0 ? usedArea / slotArea : 0;

            // Использование остатка: какая доля остатка идёт в дело (0..1)
            double utilizationRatio = remnantArea > 0 ? usedArea / remnantArea : 0;

            // Бонус за точное совпадение размеров
            double exactMatchBonus = (Math.Abs(remnantWidth - slotWidth) < 10 &&
                                     Math.Abs(remnantHeight - slotHeight) < 10) ? 0.2 : 0;

            // Итого: покрытие важнее (предпочитаем остаток, закрывающий больше слота),
            // но и утилизация важна (не тратить большой остаток на маленький слот)
            double score = coverageRatio * 0.4 + utilizationRatio * 0.4 + exactMatchBonus;

            return Math.Max(0, Math.Min(1, score));
        }

        /// <summary>
        /// Создает новые остатки из обрезков плиты по правилу двух гильотинных резов.
        /// Рез 1: горизонтальная полоса справа от использованного блока (remainingWidth × usedHeight).
        /// Рез 2: нижняя полоса на всю ширину (originalWidth × remainingHeight).
        /// Суммарная площадь двух полос = оригинальная площадь − использованная. Угловой дубликат исключён.
        /// </summary>
        public static List<Remnant> CreateRemnantsFromCut(double originalWidth, double originalHeight, 
                                                        double usedWidth, double usedHeight, bool nearWindow)
        {
            var remnants = new List<Remnant>();
            
            double remainingWidth  = originalWidth  - usedWidth;
            double remainingHeight = originalHeight - usedHeight;

            // Рез 1: горизонтальная полоса справа (usedWidth..originalWidth) × (0..usedHeight)
            if (remainingWidth >= GetMinWidth(nearWindow))
            {
                remnants.Add(new Remnant
                {
                    Width  = remainingWidth,
                    Height = usedHeight
                });
            }

            // Рез 2: нижняя полоса на всю ширину (0..originalWidth) × (usedHeight..originalHeight)
            if (remainingHeight >= GetMinHeight(nearWindow))
            {
                remnants.Add(new Remnant
                {
                    Width  = originalWidth,
                    Height = remainingHeight
                });
            }

            // Инвариант площади: сумма площадей остатков ≤ площадь исходника − использованной части
            double sourceArea  = originalWidth * originalHeight;
            double usedArea    = usedWidth     * usedHeight;
            double remnantsArea = remnants.Sum(r => r.Width * r.Height);
            if (remnantsArea > sourceArea - usedArea + 1.0) // допуск 1 мм²
            {
                System.Diagnostics.Debug.WriteLine(
                    $"WARN [RemnantManager.CreateRemnantsFromCut] remnant area sum {remnantsArea:F1} " +
                    $"> source area {sourceArea:F1} - used {usedArea:F1} = {sourceArea - usedArea:F1}");
            }

            string remList = string.Join(", ", remnants.Select(r => $"{r.Width:F0}×{r.Height:F0}"));
            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [RemnantManager.CreateRemnantsFromCut] " +
                $"orig={originalWidth:F0}×{originalHeight:F0} usedW={usedWidth:F0} usedH={usedHeight:F0} " +
                $"→ created {remnants.Count} remnants: [{remList}]");

            return remnants;
        }

        /// <summary>
        /// Получает минимальную ширину остатка
        /// </summary>
        private static double GetMinWidth(bool nearWindow)
        {
            return nearWindow ? MIN_REMNANT_HEIGHT : MIN_REMNANT_WIDTH;
        }

        /// <summary>
        /// Получает минимальную высоту остатка
        /// </summary>
        private static double GetMinHeight(bool nearWindow)
        {
            return nearWindow ? MIN_REMNANT_HEIGHT : MIN_REMNANT_WIDTH;
        }

        /// <summary>
        /// Оптимизирует базу остатков: удаляет слишком маленькие и сортирует по площади.
        /// Слияние похожих остатков отключено — MergeRemnants создавал несуществующие физически размеры.
        /// </summary>
        public static List<Remnant> OptimizeRemnantDatabase(List<Remnant> remnants)
        {
            int before = remnants.Count;

            var result = remnants
                .Where(r =>
                    (r.Width >= MIN_REMNANT_WIDTH  && r.Height >= MIN_REMNANT_WIDTH) ||
                    (r.Width >= MIN_REMNANT_HEIGHT && r.Height >= MIN_REMNANT_HEIGHT))
                .OrderByDescending(r => r.Area)
                .ToList();

            System.Diagnostics.Debug.WriteLine(
                $"DEBUG [RemnantManager.OptimizeRemnantDatabase] " +
                $"before={before} after={result.Count} (removed {before - result.Count} too-small)");

            return result;
        }

        /// <summary>
        /// Находит похожие остатки
        /// </summary>
        private static List<Remnant> FindSimilarRemnants(List<Remnant> remnants, Remnant target, int startIndex, HashSet<Guid> used)
        {
            var similar = new List<Remnant>();
            const double similarityThreshold = 0.1; // 10% различие

            for (int i = startIndex; i < remnants.Count; i++)
            {
                if (used.Contains(remnants[i].Id)) continue;

                var r = remnants[i];
                double widthDiff = Math.Abs(r.Width - target.Width) / Math.Max(r.Width, target.Width);
                double heightDiff = Math.Abs(r.Height - target.Height) / Math.Max(r.Height, target.Height);

                if (widthDiff < similarityThreshold && heightDiff < similarityThreshold)
                {
                    similar.Add(r);
                }
            }

            return similar;
        }

        /// <summary>
        /// ОТКЛЮЧЕНО: создавало несуществующий физически размер max(W1,W2) × max(H1,H2).
        /// Корректное слияние допустимо только когда одна сторона совпадает точно (физическое склеивание).
        /// Метод оставлен для справки; OptimizeRemnantDatabase его больше не вызывает.
        /// </summary>
        private static Remnant MergeRemnants(Remnant main, List<Remnant> similar)
        {
            System.Diagnostics.Debug.WriteLine(
                "DEBUG [RemnantManager.MergeRemnants] DISABLED — merge would create non-physical remnant");
            return main;
        }

        /// <summary>
        /// Получает статистику по базе остатков
        /// </summary>
        public static string GetRemnantStatistics(List<Remnant> remnants)
        {
            if (!remnants.Any())
                return "База остатков пуста";

            double totalArea = remnants.Sum(r => r.Area);
            double avgArea = totalArea / remnants.Count;
            var sizes = remnants.Select(r => $"{r.Width:F0}×{r.Height:F0}").Distinct();

            return $"Всего остатков: {remnants.Count}\n" +
                   $"Общая площадь: {totalArea / 1000000:F2} м²\n" +
                   $"Средняя площадь: {avgArea / 1000000:F2} м²\n" +
                   $"Размеры: {string.Join(", ", sizes.Take(5))}{(sizes.Count() > 5 ? "..." : "")}";
        }
    }
}