using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Clipper2Lib;
using InsulationMasterPro.Geometry;

namespace InsulationMasterPro.Geometry
{
    /// <summary>
    /// [DEPRECATED] Используется только greedy-путём (LayoutEngine.CreateLBootsForWindows).
    /// В OR-Tools pipeline L-boot элементы формируются в RollingHorizonEngine.ExtractLBootsAndCreateRemnants.
    /// </summary>
    [Obsolete("Use RollingHorizonEngine.ExtractLBootsAndCreateRemnants for OR-Tools pipeline")]
    public class LBootConfiguration
    {
        public Point2d WindowCorner { get; set; }
        public double WindowWidth { get; set; }
        public double WindowHeight { get; set; }
        public double TileWidth { get; set; }
        public double TileHeight { get; set; }
        public double OverlapSize { get; set; } = 20.0; // Перекрытие на раме 20 мм
        public string CornerType { get; set; } // "TL", "TR", "BL", "BR" (Top/Bottom Left/Right)
    }

    /// <summary>
    /// [DEPRECATED] Используется только greedy-путём (LayoutEngine.CreateLBootsForWindows).
    /// В OR-Tools pipeline L-boot элементы формируются в RollingHorizonEngine.ExtractLBootsAndCreateRemnants.
    /// </summary>
    [Obsolete("Use RollingHorizonEngine.ExtractLBootsAndCreateRemnants for OR-Tools pipeline")]
    public static class LBootGenerator
    {
        private const double MIN_BOOT_SIZE = 150.0; // Минимальный размер сапожка

        /// <summary>
        /// Создает Г-образный элемент ("сапожок") для угла оконного проема
        /// </summary>
        public static List<Polyline> CreateLBoots(Polyline window, double tileWidth, double tileHeight)
        {
            var boots = new List<Polyline>();
            var corners = GetWindowCorners(window);
            
            foreach (var corner in corners)
            {
                var config = new LBootConfiguration
                {
                    WindowCorner = corner.Point,
                    WindowWidth = corner.Width,
                    WindowHeight = corner.Height,
                    TileWidth = tileWidth,
                    TileHeight = tileHeight,
                    CornerType = corner.Type
                };

                var boot = CreateSingleLBoot(config);
                if (boot != null)
                {
                    boots.Add(boot);
                }
            }
            
            return boots;
        }

        /// <summary>
        /// Создает один Г-образный элемент для конкретного угла
        /// </summary>
        private static Polyline CreateSingleLBoot(LBootConfiguration config)
        {
            var bootSize = CalculateOptimalBootSize(config);
            if (bootSize.Width < MIN_BOOT_SIZE || bootSize.Height < MIN_BOOT_SIZE)
            {
                return null; // Слишком маленький сапожок
            }

            return CreateLShapeGeometry(config, bootSize.Width, bootSize.Height);
        }

        /// <summary>
        /// Рассчитывает оптимальные размеры Г-образного элемента
        /// </summary>
        private static (double Width, double Height) CalculateOptimalBootSize(LBootConfiguration config)
        {
            double width, height;

            switch (config.CornerType)
            {
                case "TL": // Top Left
                    width = Math.Min(config.TileWidth, config.WindowCorner.X + 200); // Зазор слева
                    height = Math.Min(config.TileHeight, GetExtentAbove(config.WindowCorner, config.WindowHeight) + 200); // Зазор сверху
                    break;
                    
                case "TR": // Top Right
                    width = Math.Min(config.TileWidth, GetExtentRight(config.WindowCorner, config.WindowWidth) + 200);
                    height = Math.Min(config.TileHeight, GetExtentAbove(config.WindowCorner, config.WindowHeight) + 200);
                    break;
                    
                case "BL": // Bottom Left
                    width = Math.Min(config.TileWidth, config.WindowCorner.X + 200);
                    height = Math.Min(config.TileHeight, GetExtentBelow(config.WindowCorner) + 200);
                    break;
                    
                case "BR": // Bottom Right
                    width = Math.Min(config.TileWidth, GetExtentRight(config.WindowCorner, config.WindowWidth) + 200);
                    height = Math.Min(config.TileHeight, GetExtentBelow(config.WindowCorner) + 200);
                    break;
                    
                default:
                    return (config.TileWidth, config.TileHeight);
            }

            // Учитываем перекрытие на раме
            width = Math.Max(MIN_BOOT_SIZE, width + config.OverlapSize);
            height = Math.Max(MIN_BOOT_SIZE, height + config.OverlapSize);

            return (width, height);
        }

        /// <summary>
        /// Создает геометрию Г-образного элемента
        /// </summary>
        private static Polyline CreateLShapeGeometry(LBootConfiguration config, double width, double height)
        {
            Point2d origin = CalculateBootOrigin(config, width, height);
            
            // Базовый прямоугольник
            Polyline baseRect = GeometryUtils.CreateRect(origin.X, origin.Y, width, height);
            
            // Прямоугольник для вырезания
            Point2d cutOrigin = CalculateCutOrigin(config, origin, width, height);
            double cutWidth = width - MIN_BOOT_SIZE;
            double cutHeight = height - MIN_BOOT_SIZE;
            
            Polyline cutRect = GeometryUtils.CreateRect(cutOrigin.X, cutOrigin.Y, cutWidth, cutHeight);
            
            // Вычитаем вырез из базового прямоугольника
            var result = GeometryUtils.BooleanSubtract(baseRect, new List<Polyline> { cutRect });
            
            return result.Count > 0 ? result[0] : null;
        }

        /// <summary>
        /// Вычисляет начальную точку Г-образного элемента
        /// </summary>
        private static Point2d CalculateBootOrigin(LBootConfiguration config, double width, double height)
        {
            return config.CornerType switch
            {
                "TL" => new Point2d(config.WindowCorner.X - width + config.OverlapSize, 
                                  config.WindowCorner.Y + config.WindowHeight + config.OverlapSize),
                "TR" => new Point2d(config.WindowCorner.X + config.WindowWidth - config.OverlapSize, 
                                  config.WindowCorner.Y + config.WindowHeight + config.OverlapSize),
                "BL" => new Point2d(config.WindowCorner.X - width + config.OverlapSize, 
                                  config.WindowCorner.Y - height + config.OverlapSize),
                "BR" => new Point2d(config.WindowCorner.X + config.WindowWidth - config.OverlapSize, 
                                  config.WindowCorner.Y - height + config.OverlapSize),
                _ => config.WindowCorner
            };
        }

        /// <summary>
        /// Вычисляет начальную точку для выреза
        /// </summary>
        private static Point2d CalculateCutOrigin(LBootConfiguration config, Point2d bootOrigin, double width, double height)
        {
            return config.CornerType switch
            {
                "TL" => bootOrigin,
                "TR" => new Point2d(bootOrigin.X + MIN_BOOT_SIZE, bootOrigin.Y),
                "BL" => new Point2d(bootOrigin.X, bootOrigin.Y + MIN_BOOT_SIZE),
                "BR" => new Point2d(bootOrigin.X + MIN_BOOT_SIZE, bootOrigin.Y + MIN_BOOT_SIZE),
                _ => bootOrigin
            };
        }

        /// <summary>
        /// Получает углы окна с их типами
        /// </summary>
        private static List<(Point2d Point, double Width, double Height, string Type)> GetWindowCorners(Polyline window)
        {
            var corners = new List<(Point2d, double, double, string)>();
            var extents = window.GeometricExtents;
            
            double minX = extents.MinPoint.X;
            double maxX = extents.MaxPoint.X;
            double minY = extents.MinPoint.Y;
            double maxY = extents.MaxPoint.Y;
            double width = maxX - minX;
            double height = maxY - minY;
            
            corners.Add((new Point2d(minX, minY), width, height, "BL")); // Bottom Left
            corners.Add((new Point2d(maxX, minY), width, height, "BR")); // Bottom Right
            corners.Add((new Point2d(minX, maxY), width, height, "TL")); // Top Left
            corners.Add((new Point2d(maxX, maxY), width, height, "TR")); // Top Right
            
            return corners;
        }

        /// <summary>
        /// Получает доступное пространство выше окна (упрощенно)
        /// </summary>
        private static double GetExtentAbove(Point2d corner, double windowHeight)
        {
            // В реальной реализации здесь должна быть проверка с другими элементами
            return 1000.0; // По умолчанию 1 метр
        }

        /// <summary>
        /// Получает доступное пространство ниже окна (упрощенно)
        /// </summary>
        private static double GetExtentBelow(Point2d corner)
        {
            // В реальной реализации здесь должна быть проверка с другими элементами
            return 1000.0; // По умолчанию 1 метр
        }

        /// <summary>
        /// Получает доступное пространство справа от окна (упрощенно)
        /// </summary>
        private static double GetExtentRight(Point2d corner, double windowWidth)
        {
            // В реальной реализации здесь должна быть проверка с другими элементами
            return 1000.0; // По умолчанию 1 метр
        }
    }
}