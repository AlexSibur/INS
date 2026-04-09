using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using InsulationMasterPro.Models;
using InsulationMasterPro.OrTools;

namespace InsulationMasterPro.Validation
{
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public static class ValidationEngine
    {
        public static ValidationResult ValidateContour(Polyline polyline, string contourType)
        {
            var result = new ValidationResult();

            // Проверка на null
            if (polyline == null)
            {
                result.IsValid = false;
                result.Errors.Add($"{contourType}: Контур не найден");
                return result;
            }

            // Проверка на замкнутость
            if (!polyline.Closed)
            {
                result.IsValid = false;
                result.Errors.Add($"{contourType}: Контур не замкнут");
            }

            // Проверка минимального количества вершин
            if (polyline.NumberOfVertices < 3)
            {
                result.IsValid = false;
                result.Errors.Add($"{contourType}: Контур должен иметь минимум 3 вершины");
            }

            // Проверка на самопересечения
            if (HasSelfIntersections(polyline))
            {
                result.IsValid = false;
                result.Errors.Add($"{contourType}: Контур имеет самопересечения");
            }

            // Проверка на вырожденность (нулевая площадь)
            double area = polyline.Area;
            if (Math.Abs(area) < 0.001)
            {
                result.IsValid = false;
                result.Errors.Add($"{contourType}: Контур имеет нулевую площадь");
            }

            // Предупреждения
            if (polyline.NumberOfVertices > 100)
            {
                result.Warnings.Add($"{contourType}: Контур имеет большое количество вершин ({polyline.NumberOfVertices}), может замедлить обработку");
            }

            if (result.Errors.Count == 0)
            {
                result.IsValid = true;
            }

            return result;
        }

        public static ValidationResult ValidateMultipleContours(List<Polyline> contours, string contourType)
        {
            var result = new ValidationResult();

            if (contours == null || contours.Count == 0)
            {
                result.IsValid = false;
                result.Errors.Add($"{contourType}: Не найдено контуров");
                return result;
            }

            for (int i = 0; i < contours.Count; i++)
            {
                var contourResult = ValidateContour(contours[i], $"{contourType} #{i + 1}");
                result.Errors.AddRange(contourResult.Errors);
                result.Warnings.AddRange(contourResult.Warnings);
            }

            // Проверка пересечений между контурами одного типа
            for (int i = 0; i < contours.Count; i++)
            {
                for (int j = i + 1; j < contours.Count; j++)
                {
                    if (ContoursIntersect(contours[i], contours[j]))
                    {
                        result.IsValid = false;
                        result.Errors.Add($"{contourType}: Контур #{i + 1} пересекается с контуром #{j + 1}");
                    }
                }
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        private static bool HasSelfIntersections(Polyline polyline)
        {
            int vertexCount = polyline.NumberOfVertices;
            
            for (int i = 0; i < vertexCount; i++)
            {
                Point2d p1 = polyline.GetPoint2dAt(i);
                Point2d p2 = polyline.GetPoint2dAt((i + 1) % vertexCount);
                
                // Проверяем пересечение с непоследующими сегментами
                for (int j = i + 2; j < vertexCount; j++)
                {
                    // Пропускаем соседний сегмент и последний/первый
                    if (j == vertexCount - 1 && i == 0) continue;
                    
                    Point2d p3 = polyline.GetPoint2dAt(j);
                    Point2d p4 = polyline.GetPoint2dAt((j + 1) % vertexCount);
                    
                    if (LineSegmentsIntersect(p1, p2, p3, p4))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }

        private static bool ContoursIntersect(Polyline contour1, Polyline contour2)
        {
            // Упрощенная проверка пересечения через ограничивающие прямоугольники
            var extents1 = contour1.GeometricExtents;
            var extents2 = contour2.GeometricExtents;
            
            if (!Extents3dOverlap(extents1, extents2))
            {
                return false;
            }
            
            // Более точная проверка через пересечение отрезков
            int vertexCount1 = contour1.NumberOfVertices;
            int vertexCount2 = contour2.NumberOfVertices;
            
            for (int i = 0; i < vertexCount1; i++)
            {
                Point2d p1 = contour1.GetPoint2dAt(i);
                Point2d p2 = contour1.GetPoint2dAt((i + 1) % vertexCount1);
                
                for (int j = 0; j < vertexCount2; j++)
                {
                    Point2d p3 = contour2.GetPoint2dAt(j);
                    Point2d p4 = contour2.GetPoint2dAt((j + 1) % vertexCount2);
                    
                    if (LineSegmentsIntersect(p1, p2, p3, p4))
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }

        private static bool Extents3dOverlap(Extents3d a, Extents3d b)
        {
            return a.MinPoint.X <= b.MaxPoint.X && a.MaxPoint.X >= b.MinPoint.X &&
                   a.MinPoint.Y <= b.MaxPoint.Y && a.MaxPoint.Y >= b.MinPoint.Y &&
                   a.MinPoint.Z <= b.MaxPoint.Z && a.MaxPoint.Z >= b.MinPoint.Z;
        }

        private static bool LineSegmentsIntersect(Point2d p1, Point2d p2, Point2d p3, Point2d p4)
        {
            // Используем ориентацию для проверки пересечения отрезков
            double orientation(Point2d a, Point2d b, Point2d c)
            {
                return (c.Y - a.Y) * (b.X - a.X) - (b.Y - a.Y) * (c.X - a.X);
            }

            double o1 = orientation(p1, p2, p3);
            double o2 = orientation(p1, p2, p4);
            double o3 = orientation(p3, p4, p1);
            double o4 = orientation(p3, p4, p2);

            // Общий случай
            if ((o1 > 0 && o2 < 0) || (o1 < 0 && o2 > 0) || 
                (o3 > 0 && o4 < 0) || (o3 < 0 && o4 > 0))
            {
                return true;
            }

            // Коллинеарные случаи
            const double epsilon = 1e-10;
            if (Math.Abs(o1) < epsilon && OnSegment(p1, p3, p2)) return true;
            if (Math.Abs(o2) < epsilon && OnSegment(p1, p4, p2)) return true;
            if (Math.Abs(o3) < epsilon && OnSegment(p3, p1, p4)) return true;
            if (Math.Abs(o4) < epsilon && OnSegment(p3, p2, p4)) return true;

            return false;
        }

        private static bool OnSegment(Point2d p, Point2d q, Point2d r)
        {
            const double epsilon = 1e-10;
            return q.X <= Math.Max(p.X, r.X) + epsilon && q.X >= Math.Min(p.X, r.X) - epsilon &&
                   q.Y <= Math.Max(p.Y, r.Y) + epsilon && q.Y >= Math.Min(p.Y, r.Y) - epsilon;
        }
        public static void ValidateWindowOverlap(Polyline facade, List<WindowInfo> windows)
        {
            double facadeMinY = facade.GeometricExtents.MinPoint.Y;
            double facadeHeight = facade.GeometricExtents.MaxPoint.Y - facadeMinY;

            foreach (var w in windows)
            {
                double wkeffBottom = w.MinY - facadeMinY;  // должно быть > 0
                double wkeffTop    = w.MaxY - facadeMinY;  // должно быть < H_facade

                if (wkeffBottom < 0 || wkeffTop < 0)
                    throw new InvalidOperationException(
                        $"Координаты окна {w.Id} не нормализованы: " +
                        $"wkeff.bottom={wkeffBottom:F1}мм, wkeff.top={wkeffTop:F1}мм");
            }
        }
    }
}