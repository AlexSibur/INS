using System;
using System.Collections.Generic;
using System.Linq;
using Google.OrTools.Sat;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.OrTools
{
    public class RowHeightForecaster
    {
        private const int SCALE = 1;

        /// <summary>
        /// Прогнозирует высоты рядов фасада через CP-SAT.
        /// Проход 1: жёсткий запрет мёртвой зоны (h == 600 ИЛИ h ≤ 450).
        /// Проход 2 (fallback): тяжёлый штраф вместо запрета, если pass1 даёт INFEASIBLE.
        /// </summary>
        public static List<RowDefinition>? Forecast(
            double facadeWidth, double facadeHeight,
            double minY,
            List<WindowInfo> windows,
            List<Remnant> stock,
            OptimizationConstraints constraints,
            bool firstRowWithRelease)
        {
            // Pass 1: запрещаем мёртвую зону — все обрезки либо 0 (H=600) либо ≥ MinRemnantToSave (H≤450)
            var result = ForecastInternal(facadeWidth, facadeHeight, minY, windows, stock, constraints, firstRowWithRelease, enforceDeadZoneBan: true);
            if (result != null) return result;

            // Pass 2: геометрия окон не позволила использовать запрет — переходим на тяжёлый штраф
            System.Diagnostics.Debug.WriteLine("[RowHeightForecaster] Жёсткий запрет мёртвой зоны → INFEASIBLE, переключение на тяжёлый штраф");
            return ForecastInternal(facadeWidth, facadeHeight, minY, windows, stock, constraints, firstRowWithRelease, enforceDeadZoneBan: false);
        }

        private static List<RowDefinition>? ForecastInternal(
            double facadeWidth, double facadeHeight,
            double minY,
            List<WindowInfo> windows,
            List<Remnant> stock,
            OptimizationConstraints constraints,
            bool firstRowWithRelease,
            bool enforceDeadZoneBan)
        {
            int H = (int)Math.Round(facadeHeight * SCALE);
            int hBase = (int)Math.Round(constraints.TileHeight * SCALE);
            int hMin = (int)Math.Round(constraints.MinRowHeight * SCALE);
            int minRemnant = (int)Math.Round(constraints.MinRemnantToSave * SCALE);

            var model = new CpModel();
            int maxRows = (int)Math.Ceiling(facadeHeight / constraints.MinRowHeight) + 2;

            var stockHeights = new HashSet<int>();
            var availableWidthAtHeight = new Dictionary<int, double>();

            foreach (var r in stock)
            {
                int rh = (int)Math.Round(r.Height * SCALE);
                int rw = (int)Math.Round(r.Width * SCALE);
                if (rh >= hMin && rh <= hBase)
                {
                    stockHeights.Add(rh);
                    availableWidthAtHeight[rh] = availableWidthAtHeight.GetValueOrDefault(rh) + r.Width;
                }
                if (rw >= hMin && rw <= hBase)
                {
                    stockHeights.Add(rw);
                    availableWidthAtHeight[rw] = availableWidthAtHeight.GetValueOrDefault(rw) + r.Height;
                }
            }
            var stockList = stockHeights.OrderByDescending(x => x).ToList();

            var h = new IntVar[maxRows];
            var y = new IntVar[maxRows + 1];
            var active = new BoolVar[maxRows];

            var isStock = new BoolVar[maxRows, stockList.Count > 0 ? stockList.Count : 1];

            y[0] = model.NewConstant(0);
            for (int i = 0; i < maxRows; i++)
            {
                h[i] = model.NewIntVar(0, hBase, $"h_{i}");
                active[i] = model.NewBoolVar($"active_{i}");
                y[i + 1] = model.NewIntVar(0, H, $"y_{i + 1}");

                model.Add(y[i + 1] == y[i] + h[i]);
                model.Add(h[i] == 0).OnlyEnforceIf(active[i].Not());
                model.Add(h[i] >= hMin).OnlyEnforceIf(active[i]);

                for (int sIdx = 0; sIdx < stockList.Count; sIdx++)
                {
                    isStock[i, sIdx] = model.NewBoolVar($"isStock_{i}_{sIdx}");
                    model.Add(h[i] == stockList[sIdx]).OnlyEnforceIf(isStock[i, sIdx]);
                    model.Add(h[i] != stockList[sIdx]).OnlyEnforceIf(isStock[i, sIdx].Not());
                }
            }

            for (int i = 0; i < maxRows - 1; i++)
                model.AddImplication(active[i + 1], active[i]);

            model.Add(y[maxRows] == H);

            // Ограничение сверху на число рядов: не более ceil(H/TileHeight)+2 активных рядов.
            // Ожидаемое число рядов = ceil(H/600). Допускаем +2 для краевых случаев (нецелые высоты).
            // Это предотвращает расширение в сторону мелких лишних рядов при жёстком запрете мёртвой зоны.
            int expectedMaxRows = (int)Math.Ceiling(facadeHeight / constraints.TileHeight) + 2;
            if (expectedMaxRows < maxRows)
            {
                var activeSum = new List<LinearExpr>();
                for (int i = 0; i < maxRows; i++)
                    activeSum.Add(active[i]);
                model.Add(LinearExpr.Sum(activeSum) <= expectedMaxRows);
            }

            int overlapScaled = (int)Math.Round(constraints.WindowOverlap * SCALE);
            int safeMargin = hMin - overlapScaled; // 150 - 20 = 130

            foreach (var win in windows)
            {
                int awPhys = (int)Math.Round((win.MinY - minY) * SCALE);
                int bwPhys = (int)Math.Round((win.MaxY - minY) * SCALE);

                for (int i = 1; i <= maxRows; i++)
                {
                    var isBelowSafe = model.NewBoolVar("");
                    model.Add(y[i] <= awPhys - safeMargin).OnlyEnforceIf(isBelowSafe);
                    model.Add(y[i] > awPhys - safeMargin).OnlyEnforceIf(isBelowSafe.Not());

                    var isAboveSafe = model.NewBoolVar("");
                    var c1 = model.NewBoolVar("");
                    model.Add(y[i] >= bwPhys + safeMargin).OnlyEnforceIf(c1);
                    model.Add(y[i] < bwPhys + safeMargin).OnlyEnforceIf(c1.Not());

                    var c2 = model.NewBoolVar("");
                    model.Add(y[i] == H).OnlyEnforceIf(c2);
                    model.Add(y[i] != H).OnlyEnforceIf(c2.Not());

                    if (H >= bwPhys) {
                        model.AddBoolOr(new ILiteral[] { c1, c2 }).OnlyEnforceIf(isAboveSafe);
                        model.AddBoolAnd(new ILiteral[] { c1.Not(), c2.Not() }).OnlyEnforceIf(isAboveSafe.Not());
                    } else {
                        model.AddBoolAnd(new ILiteral[] { c1 }).OnlyEnforceIf(isAboveSafe);
                        model.AddBoolOr(new ILiteral[] { c1.Not() }).OnlyEnforceIf(isAboveSafe.Not());
                    }

                    var isInsideSafe = model.NewBoolVar("");
                    var inA = model.NewBoolVar("");
                    model.Add(y[i] >= awPhys + safeMargin).OnlyEnforceIf(inA);
                    model.Add(y[i] < awPhys + safeMargin).OnlyEnforceIf(inA.Not());
                    var inB = model.NewBoolVar("");
                    model.Add(y[i] <= bwPhys - safeMargin).OnlyEnforceIf(inB);
                    model.Add(y[i] > bwPhys - safeMargin).OnlyEnforceIf(inB.Not());

                    model.AddBoolAnd(new ILiteral[] { inA, inB }).OnlyEnforceIf(isInsideSafe);
                    model.AddBoolOr(new ILiteral[] { inA.Not(), inB.Not() }).OnlyEnforceIf(isInsideSafe.Not());

                    model.AddBoolOr(new ILiteral[] { isBelowSafe, isAboveSafe, isInsideSafe })
                         .OnlyEnforceIf(active[i - 1]);
                }
            }

            var objTerms = new List<LinearExpr>();
            for (int i = 0; i < maxRows; i++)
            {
                var diffFromStandard = model.NewIntVar(0, hBase, $"diffStd_{i}");
                model.AddAbsEquality(diffFromStandard, h[i] - hBase);
                long rowPosWeight = (maxRows - i) * 100;
                objTerms.Add(diffFromStandard * rowPosWeight);

                // (Stock reward logic moved outside the row loop to enforce capacity limits)

                objTerms.Add(active[i] * 1000);

                // Определяем попадание в мёртвую зону: 0 < leftover < minRemnant → h ∈ (hBase-minRemnant, hBase)
                {
                    var leftover = model.NewIntVar(0, hBase, $"leftover_{i}");
                    model.Add(leftover == hBase - h[i]).OnlyEnforceIf(active[i]);
                    model.Add(leftover == 0).OnlyEnforceIf(active[i].Not());

                    var isWasteful = model.NewBoolVar($"isWaste_{i}");
                    model.Add(leftover > 0).OnlyEnforceIf(isWasteful);
                    model.Add(leftover <= 0).OnlyEnforceIf(isWasteful.Not());

                    var isBelowSave = model.NewBoolVar($"isBelowSave_{i}");
                    model.Add(leftover < minRemnant).OnlyEnforceIf(isBelowSave);
                    model.Add(leftover >= minRemnant).OnlyEnforceIf(isBelowSave.Not());

                    var inDeadZone = model.NewBoolVar($"inDZ_{i}");
                    model.AddBoolAnd(new ILiteral[] { isWasteful, isBelowSave }).OnlyEnforceIf(inDeadZone);
                    model.AddBoolOr(new ILiteral[] { isWasteful.Not(), isBelowSave.Not() }).OnlyEnforceIf(inDeadZone.Not());

                    if (enforceDeadZoneBan)
                    {
                        // Жёсткий запрет: активный ряд не может попасть в мёртвую зону
                        // Эквивалентно: h[i] == hBase ИЛИ h[i] <= hBase - minRemnant
                        model.AddBoolOr(new ILiteral[] { inDeadZone.Not(), active[i].Not() });
                    }
                    else
                    {
                        // Fallback: тяжёлый штраф пропорционально позиции ряда
                        // Делает мёртвую зону крайне невыгодной, но не невозможной
                        int dzWeightInt = (int)Math.Min(10L * rowPosWeight, 2_000_000L);
                        var dzPenalty = model.NewIntVar(0, (long)hBase * dzWeightInt, $"dzPen_{i}");
                        model.Add(dzPenalty == leftover * dzWeightInt).OnlyEnforceIf(inDeadZone);
                        model.Add(dzPenalty == 0).OnlyEnforceIf(inDeadZone.Not());
                        objTerms.Add(dzPenalty);
                    }
                }
            }

            // Вариант Б: Применяем награду за склад с учетом лимита на количество рядов
            for (int sIdx = 0; sIdx < stockList.Count; sIdx++)
            {
                double stockCoverageRatio = availableWidthAtHeight[stockList[sIdx]] / facadeWidth;
                int maxRewardedRows = (int)Math.Ceiling(stockCoverageRatio);
                
                // Начинаем давать супер-награду, только если остатков хватает хотя бы на 30% ряда
                if (maxRewardedRows == 0 || stockCoverageRatio < 0.3) 
                    continue;

                var receivesRewardVars = new List<BoolVar>();
                for (int i = 0; i < maxRows; i++)
                {
                    var receivesReward = model.NewBoolVar($"recRew_{i}_{sIdx}");
                    // Награду можно получить только если ряд имеет высоту этого остатка
                    model.AddImplication(receivesReward, isStock[i, sIdx]);
                    receivesRewardVars.Add(receivesReward);
                    
                    long rowPosWeight = (maxRows - i) * 100;
                    // Макс отклонение для ряда 150мм = 450 * rowPosWeight. Награда 5000 гарантированно перебьёт штраф!
                    double rewardScale = stockCoverageRatio >= 0.8 ? 5000.0 : 2000.0;
                    long stockReward = (long)(Math.Min(1.0, stockCoverageRatio) * rowPosWeight * rewardScale);
                    objTerms.Add(receivesReward * (-stockReward));
                }
                
                // Ограничиваем суммарное количество рядов, которые могут "съесть" эту награду
                var sumExpr = new List<LinearExpr>();
                foreach(var v in receivesRewardVars) sumExpr.Add(v);
                model.Add(LinearExpr.Sum(sumExpr) <= maxRewardedRows);
            }

            // При жёстком запрете мёртвой зоны солвер работает в более ограниченном пространстве —
            // ему требуется больше времени, чтобы найти оптимальное (не просто допустимое) решение.
            // Минимальный таймаут: 15 с для hard-ban, 5 с для fallback.
            model.Minimize(LinearExpr.Sum(objTerms));

            var solver = new CpSolver();
            int baseTimeout = 5;
            var type = constraints.GetType();
            var prop = type.GetProperty("RowHeightTimeoutSeconds");
            if (prop != null)
                baseTimeout = (int)prop.GetValue(constraints);

            int timeout = enforceDeadZoneBan
                ? Math.Max(baseTimeout * 3, 15)
                : baseTimeout;
            solver.StringParameters = $"max_time_in_seconds:{timeout};relative_gap_limit:0.01";

            if (solver.Solve(model) is var status && (status == CpSolverStatus.Optimal || status == CpSolverStatus.Feasible))
            {
                var result = new List<RowDefinition>();
                for (int i = 0; i < maxRows; i++)
                {
                    if (solver.BooleanValue(active[i]))
                    {
                        result.Add(new RowDefinition {
                            RowIndex = i,
                            Y = solver.Value(y[i]) / (double)SCALE + minY,
                            Height = solver.Value(h[i]) / (double)SCALE,
                            HasRelease = firstRowWithRelease ? (i % 2 == 0) : (i % 2 == 1)
                        });
                    }
                }
                return result;
            }
            return null;
        }
    }
}
