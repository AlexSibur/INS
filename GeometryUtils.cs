using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Clipper2Lib;

namespace InsulationMasterPro.Geometry
{
    public static class GeometryUtils
    {
        private const double COORDINATE_SCALE = 1000.0; // Scale factor for Clipper (mm -> internal units)

        public static PathsD PolylineToPathsD(Polyline pl)
        {
            PathD path = new PathD();
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                Point2d pt = pl.GetPoint2dAt(i);
                // Scale coordinates for better precision with Clipper
                path.Add(new PointD(pt.X * COORDINATE_SCALE, pt.Y * COORDINATE_SCALE));
            }
            return new PathsD { path };
        }

        public static Polyline PathsDToPolyline(PathsD paths)
        {
            if (paths.Count == 0 || paths[0].Count == 0) return null;
            
            // Take the first path (simplification - real implementation should handle holes/islands)
            Polyline pl = new Polyline();
            for (int i = 0; i < paths[0].Count; i++)
            {
                pl.AddVertexAt(i, new Point2d(paths[0][i].x / COORDINATE_SCALE, paths[0][i].y / COORDINATE_SCALE), 0, 0, 0);
            }
            pl.Closed = true;
            return pl;
        }

        public static List<Polyline> PathsDToPolylines(PathsD paths)
        {
            var result = new List<Polyline>();
            
            foreach (var path in paths)
            {
                if (path.Count < 3) continue;
                
                Polyline pl = new Polyline();
                for (int i = 0; i < path.Count; i++)
                {
                    pl.AddVertexAt(i, new Point2d(path[i].x / COORDINATE_SCALE, path[i].y / COORDINATE_SCALE), 0, 0, 0);
                }
                pl.Closed = true;
                result.Add(pl);
            }
            
            return result;
        }

        public static List<Polyline> BooleanSubtract(Polyline main, List<Polyline> subtractions)
        {
            if (main == null || subtractions == null || !subtractions.Any())
                return new List<Polyline> { main };

            try
            {
                PathsD subject = PolylineToPathsD(main);
                PathsD clip = new PathsD();
                foreach (var sub in subtractions)
                {
                    if (sub != null)
                        clip.AddRange(PolylineToPathsD(sub));
                }

                PathsD solution = Clipper.Difference(subject, clip, FillRule.EvenOdd);
                return PathsDToPolylines(solution);
            }
            catch
            {
                // В случае ошибки возвращаем исходный полигон
                return new List<Polyline> { main };
            }
        }

        public static List<Polyline> BooleanIntersection(Polyline poly1, List<Polyline> polys2)
        {
            if (poly1 == null) return new List<Polyline>();
            if (polys2 == null || !polys2.Any()) return new List<Polyline> { poly1 };

            try
            {
                PathsD subject = PolylineToPathsD(poly1);
                PathsD clip = new PathsD();
                
                foreach (var poly in polys2)
                {
                    if (poly != null)
                        clip.AddRange(PolylineToPathsD(poly));
                }

                PathsD solution = Clipper.Intersect(subject, clip, FillRule.EvenOdd);
                return PathsDToPolylines(solution);
            }
            catch
            {
                return new List<Polyline>();
            }
        }

        /// <summary>
        /// Объединение нескольких полигонов (для расширения фасада на выпуски).
        /// </summary>
        public static List<Polyline> BooleanUnion(List<Polyline> polys)
        {
            if (polys == null || polys.Count == 0) return new List<Polyline>();
            if (polys.Count == 1) return new List<Polyline> { polys[0] };
            try
            {
                PathsD acc = PolylineToPathsD(polys[0]);
                for (int i = 1; i < polys.Count; i++)
                {
                    if (polys[i] == null) continue;
                    PathsD next = PolylineToPathsD(polys[i]);
                    acc = Clipper.Union(acc, next, FillRule.EvenOdd);
                }
                return PathsDToPolylines(acc);
            }
            catch
            {
                return new List<Polyline> { polys[0] };
            }
        }

        /// <summary>
        /// Расширяет контур фасада влево и вправо на заданную величину (для выпусков за контур). Выпуски тоже обрезаются по окнам.
        /// </summary>
        public static Polyline ExtendFacadeLeftRight(Polyline facade, double overhang)
        {
            if (facade == null || overhang <= 0) return facade;
            var ext = facade.GeometricExtents;
            double minX = ext.MinPoint.X;
            double maxX = ext.MaxPoint.X;
            double minY = ext.MinPoint.Y;
            double maxY = ext.MaxPoint.Y;
            double h = maxY - minY;
            Polyline leftStrip = CreateRect(minX - overhang, minY, overhang, h);
            Polyline rightStrip = CreateRect(maxX, minY, overhang, h);
            var union = BooleanUnion(new List<Polyline> { facade, leftStrip, rightStrip });
            return union.Count > 0 ? union[0] : facade;
        }

        public static Polyline CreateRect(double x, double y, double w, double h)
        {
            Polyline pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(x, y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(x + w, y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(x + w, y + h), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(x, y + h), 0, 0, 0);
            pl.Closed = true;
            return pl;
        }

        public static Polyline CreateRect(Point2d origin, double w, double h)
        {
            return CreateRect(origin.X, origin.Y, w, h);
        }

        public static Polyline CreateRect(Point3d origin, double w, double h)
        {
            return CreateRect(origin.X, origin.Y, w, h);
        }

        public static Polyline CreateCenteredRect(Point2d center, double w, double h)
        {
            return CreateRect(center.X - w/2, center.Y - h/2, w, h);
        }

        /// <summary>
        /// Проверяет, находится ли точка внутри полигона
        /// </summary>
        public static bool IsPointInside(Polyline polygon, Point2d point)
        {
            if (!polygon.Closed) return false;

            var pointPath = new PathsD { new PathD { new PointD(point.X * COORDINATE_SCALE, point.Y * COORDINATE_SCALE) } };
            var polyPath = PolylineToPathsD(polygon);
            if (polyPath == null || polyPath.Count == 0 || polyPath[0].Count == 0) return false;
            var result = Clipper.PointInPolygon(pointPath[0][0], polyPath[0]);
            return result != PointInPolygonResult.IsOutside;
        }

        /// <summary>
        /// Получает ограничивающий прямоугольник
        /// </summary>
        public static Extents2d GetBoundingBox(Polyline polyline)
        {
            var extents = polyline.GeometricExtents;
            return new Extents2d(
                new Point2d(extents.MinPoint.X, extents.MinPoint.Y),
                new Point2d(extents.MaxPoint.X, extents.MaxPoint.Y)
            );
        }

        /// <summary>
        /// Проверяет пересечение двух полилиний
        /// </summary>
        private static bool Extents2dOverlap(Extents2d a, Extents2d b)
        {
            return a.MinPoint.X <= b.MaxPoint.X && a.MaxPoint.X >= b.MinPoint.X &&
                   a.MinPoint.Y <= b.MaxPoint.Y && a.MaxPoint.Y >= b.MinPoint.Y;
        }

        public static bool Intersects(Polyline poly1, Polyline poly2)
        {
            var bbox1 = GetBoundingBox(poly1);
            var bbox2 = GetBoundingBox(poly2);
            
            if (!Extents2dOverlap(bbox1, bbox2))
                return false;

            // Более точная проверка через Clipper
            var intersection = BooleanIntersection(poly1, new List<Polyline> { poly2 });
            return intersection.Any() && intersection.First().Area > 0.001;
        }

        /// <summary>
        /// Упрощает геометрию (удаляет лишние точки)
        /// </summary>
        public static Polyline Simplify(Polyline polyline, double tolerance = 1.0)
        {
            try
            {
                var paths = PolylineToPathsD(polyline);
                var simplified = Clipper.SimplifyPaths(paths, tolerance * COORDINATE_SCALE, false);
                return PathsDToPolyline(simplified);
            }
            catch
            {
                return polyline;
            }
        }

        /// <summary>
        /// Получает центр тяжести полигона
        /// </summary>
        public static Point2d GetCentroid(Polyline polyline)
        {
            var extents = polyline.GeometricExtents;
            return new Point2d(
                (extents.MinPoint.X + extents.MaxPoint.X) / 2,
                (extents.MinPoint.Y + extents.MaxPoint.Y) / 2
            );
        }

        /// <summary>
        /// Расширяет полигон на указанное расстояние
        /// </summary>
        public static List<Polyline> Offset(Polyline polyline, double offset)
        {
            try
            {
                var paths = PolylineToPathsD(polyline);
                var offsetPaths = Clipper.InflatePaths(paths, offset * COORDINATE_SCALE, JoinType.Miter, EndType.Polygon);
                return PathsDToPolylines(offsetPaths);
            }
            catch
            {
                return new List<Polyline> { polyline };
            }
        }

        /// <summary>
        /// Устаревший метод для создания Г-образного элемента
        /// Используйте LBootGenerator вместо этого
        /// </summary>
        [Obsolete("Use LBootGenerator.CreateLBoots instead")]
        public static List<Polyline> CreateLBoot(Point2d corner, double w, double h, double cutW, double cutH)
        {
            Polyline baseTile = CreateRect(corner.X, corner.Y, w, h);
            Polyline cutRect = CreateRect(corner.X, corner.Y, cutW, cutH);
            
            return BooleanSubtract(baseTile, new List<Polyline> { cutRect });
        }

        /// <summary>
        /// Проверяет валидность полилинии
        /// </summary>
        public static bool IsValid(Polyline polyline)
        {
            return polyline != null && 
                   polyline.NumberOfVertices >= 3 && 
                   polyline.Closed && 
                   polyline.Area > 0.001 &&
                   !HasSelfIntersections(polyline);
        }

        /// <summary>
        /// Проверяет наличие самопересечений
        /// </summary>
        private static bool HasSelfIntersections(Polyline polyline)
        {
            // Упрощенная проверка - в реальной реализации нужна более сложная логика
            var paths = PolylineToPathsD(polyline);
            var simplified = Clipper.SimplifyPaths(paths, 0.1 * COORDINATE_SCALE, false);
            return simplified.Count == 0 || (simplified.Count == 1 && simplified[0].Count < polyline.NumberOfVertices);
        }
    }
}
