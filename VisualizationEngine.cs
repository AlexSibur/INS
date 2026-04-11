using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.Visualization
{
    public class TileVisualization
    {
        public Polyline Geometry { get; set; }
        public string Layer { get; set; }
        public string Annotation { get; set; }
        public Point3d AnnotationPosition { get; set; }
        public bool IsReused { get; set; }
        /// <summary>Плита с выпуском за контур.</summary>
        public bool HasRelease { get; set; }
        /// <summary>Номер блока в формате X.Y.NNN (новая плита) или X.Y.NNN.ZZ (остаток). Пример: 1.3.005, 1.3.005.01.</summary>
        public string BlockNumber { get; set; } = string.Empty;
    }

    public static class VisualizationEngine
    {
        private const string NEW_TILES_LAYER = "INS_NEW";
        private const string REUSED_TILES_LAYER = "INS_REUSE";
        private const string ANNOTATIONS_LAYER = "INS_ANNOTATIONS";
        private const string HATCH_LAYER = "INS_HATCH";
        private const string OVERLAP_LAYER = "INS_OVERLAP";
        private const string LBOOT_LAYER = "INS_LBOOT";
        private const string FACADE_LABELS_LAYER = "INS_FACADE_LABELS";
        private const string ANNOTATION_STYLE = "Standard";

        /// <summary>
        /// Создает все слои, необходимые для визуализации
        /// </summary>
        public static void CreateLayers(Database db, Transaction tr)
        {
            CreateLayer(db, tr, NEW_TILES_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 3)); // Зеленый
            CreateLayer(db, tr, REUSED_TILES_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 2)); // Желтый
            CreateLayer(db, tr, ANNOTATIONS_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 7)); // Белый
            CreateLayer(db, tr, HATCH_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 1)); // Красный
            CreateLayer(db, tr, OVERLAP_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 4)); // Голубой
            CreateLayer(db, tr, LBOOT_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 6)); // Magenta
            CreateLayer(db, tr, FACADE_LABELS_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 2)); // Жёлтый
            CreateLayer(db, tr, OVERLAP_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 4)); // Голубой — зона нахлёста 20 мм
            CreateLayer(db, tr, LBOOT_LAYER, Color.FromColorIndex(ColorMethod.ByAci, 6)); // Magenta — L-boot элементы
        }

        /// <summary>
        /// Отрисовывает все плиты и аннотации в чертеже
        /// </summary>
        public static void VisualizeLayout(Database db, Transaction tr, List<TileVisualization> tiles)
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (var tile in tiles)
            {
                // Создаем полилинию плиты
                var polyline = new Polyline();
                
                // Копируем геометрию
                for (int i = 0; i < tile.Geometry.NumberOfVertices; i++)
                {
                    var point2d = tile.Geometry.GetPoint2dAt(i);
                    var bulge = tile.Geometry.GetBulgeAt(i);
                    polyline.AddVertexAt(i, point2d, bulge, 0, 0);
                }
                
                polyline.Closed = tile.Geometry.Closed;
                polyline.Layer = tile.IsReused ? REUSED_TILES_LAYER : NEW_TILES_LAYER;
                
                // Цвет по слою (новые — зелёный, остатки — жёлтый)
                polyline.ColorIndex = tile.IsReused ? 2 : 3;
                
                // Добавляем в модель
                modelSpace.AppendEntity(polyline);
                tr.AddNewlyCreatedDBObject(polyline, true);
                
                // Добавляем аннотацию
                if (!string.IsNullOrEmpty(tile.Annotation))
                {
                    CreateAnnotation(tr, modelSpace, tile.AnnotationPosition, tile.Annotation);
                }
            }
        }

        /// <summary>
        /// Создает объект визуализации для плиты (аннотация — только размер, без подписи «выпуск»).
        /// </summary>
        public static TileVisualization CreateTileVisualization(Polyline tileGeometry, bool isReused, string dimensions, bool hasRelease = false)
        {
            var extent = tileGeometry.GeometricExtents;
            var center = new Point3d(
                (extent.MinPoint.X + extent.MaxPoint.X) / 2,
                (extent.MinPoint.Y + extent.MaxPoint.Y) / 2,
                0);

            return new TileVisualization
            {
                Geometry = tileGeometry,
                Layer = isReused ? REUSED_TILES_LAYER : NEW_TILES_LAYER,
                Annotation = dimensions,
                AnnotationPosition = center,
                IsReused = isReused,
                HasRelease = hasRelease
            };
        }

        /// <summary>
        /// Создает многострочную текстовую аннотацию (MText, 60 мм)
        /// </summary>
        private static void CreateAnnotation(Transaction tr, BlockTableRecord modelSpace, Point3d position, string text)
        {
            var mtext = new MText
            {
                Location = position,
                Contents = text,
                TextHeight = 60, // Высота текста 60 мм
                Layer = ANNOTATIONS_LAYER,
                Attachment = AttachmentPoint.MiddleCenter
            };

            modelSpace.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
        }

        /// <summary>
        /// Создает слой с указанными параметрами
        /// </summary>
        private static void CreateLayer(Database db, Transaction tr, string name, Color color)
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            
            if (!layerTable.Has(name))
            {
                layerTable.UpgradeOpen();
                var layerTableRecord = new LayerTableRecord
                {
                    Name = name,
                    Color = color,
                    IsPlottable = true,
                    IsOff = false,
                    IsFrozen = false
                };
                
                layerTable.Add(layerTableRecord);
                tr.AddNewlyCreatedDBObject(layerTableRecord, true);
            }
            else
            {
                // Слой существует, обновляем его свойства
                var layerTableRecord = (LayerTableRecord)tr.GetObject(layerTable[name], OpenMode.ForWrite);
                layerTableRecord.Color = color;
            }
        }

        /// <summary>
        /// Форматирует размер для аннотации (ширина × высота, мм).
        /// </summary>
        public static string FormatDimensions(double width, double height)
        {
            return $"{width:F0}x{height:F0}";
        }

        /// <summary>
        /// Форматирует полную аннотацию: размер + номер блока (две строки MText).
        /// Строка 1: размеры, например "1200x600". Строка 2: номер блока, например "1.3.005" или "1.3.005.01".
        /// </summary>
        public static string FormatAnnotation(double width, double height, string displayNumber)
        {
            string dims = $"{width:F0}x{height:F0}";
            if (string.IsNullOrEmpty(displayNumber))
                return dims;
            // MText использует \P как перевод строки
            return $"{dims}\\P{displayNumber}";
        }

        /// <summary>
        /// Визуализирует области штриховки — перекрытия блоков с оконными проемами (ТЗ п.2.7).
        /// Блоки в верхнем/нижнем рядах окна НЕ обрезаются, а перекрываемые части штрихуются.
        /// </summary>
        public static void VisualizeHatchRegions(Database db, Transaction tr, List<Polyline> hatchRegions)
        {
            if (hatchRegions == null || !hatchRegions.Any()) return;

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (var region in hatchRegions)
            {
                if (region == null || region.NumberOfVertices < 3) continue;

                try
                {
                    // 1. Добавляем контурную полилинию на слой штриховки
                    var boundary = new Polyline();
                    for (int i = 0; i < region.NumberOfVertices; i++)
                    {
                        boundary.AddVertexAt(i, region.GetPoint2dAt(i), region.GetBulgeAt(i), 0, 0);
                    }
                    boundary.Closed = true;
                    boundary.Layer = HATCH_LAYER;
                    boundary.ColorIndex = 1; // Красный
                    modelSpace.AppendEntity(boundary);
                    tr.AddNewlyCreatedDBObject(boundary, true);

                    // 2. Создаём штриховку по контуру
                    var hatch = new Hatch();
                    hatch.SetDatabaseDefaults();
                    hatch.Layer = HATCH_LAYER;
                    hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");
                    hatch.Associative = false;
                    hatch.HatchStyle = HatchStyle.Normal;
                    hatch.PatternScale = 22.0; // шаг штриховки ≈ 70 мм (ANSI31 базовый шаг ≈ 3.175 мм × 22 ≈ 70)
                    modelSpace.AppendEntity(hatch);
                    tr.AddNewlyCreatedDBObject(hatch, true);

                    hatch.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection(new[] { boundary.ObjectId }));
                    hatch.EvaluateHatch(true);
                }
                catch
                {
                    // Штриховка может не создаться для сложных контуров — продолжаем
                }
            }
        }

        /// <summary>
        /// Визуализирует зоны нахлёста вокруг окон (ТЗ v13.3 §7.2).
        /// Для каждого окна рисует контур нахлёста 20 мм ВНУТРЬ оконного проёма.
        /// </summary>
        public static void VisualizeOverlapZones(Database db, Transaction tr, List<Polyline> overlapRegions)
        {
            if (overlapRegions == null || !overlapRegions.Any()) return;

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (var region in overlapRegions)
            {
                if (region == null || region.NumberOfVertices < 3) continue;

                var boundary = new Polyline();
                for (int i = 0; i < region.NumberOfVertices; i++)
                {
                    boundary.AddVertexAt(i, region.GetPoint2dAt(i), region.GetBulgeAt(i), 0, 0);
                }
                boundary.Closed = true;
                boundary.Layer = OVERLAP_LAYER;
                boundary.ColorIndex = 4; // Голубой
                modelSpace.AppendEntity(boundary);
                tr.AddNewlyCreatedDBObject(boundary, true);
            }
        }

        /// <summary>
        /// Визуализирует L-boot элементы на отдельном слое (ТЗ v13.3 §12).
        /// </summary>
        public static void VisualizeLBootElements(Database db, Transaction tr, List<Polyline> lbootGeometries)
        {
            if (lbootGeometries == null || !lbootGeometries.Any()) return;

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (var geom in lbootGeometries)
            {
                if (geom == null || geom.NumberOfVertices < 3) continue;

                var boundary = new Polyline();
                for (int i = 0; i < geom.NumberOfVertices; i++)
                {
                    boundary.AddVertexAt(i, geom.GetPoint2dAt(i), geom.GetBulgeAt(i), 0, 0);
                }
                boundary.Closed = true;
                boundary.Layer = LBOOT_LAYER;
                boundary.ColorIndex = 6; // Magenta
                boundary.LineWeight = LineWeight.LineWeight050; // Толстая линия для L-boot
                modelSpace.AppendEntity(boundary);
                tr.AddNewlyCreatedDBObject(boundary, true);
            }
        }

        /// <summary>
        /// Очищает старые результаты раскладки
        /// </summary>
        public static void CleanOldLayout(Database db, Transaction tr)
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            var objectsToDelete = new List<ObjectId>();

            foreach (ObjectId objId in modelSpace)
            {
                var entity = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                if (entity != null &&
                    (entity.Layer == NEW_TILES_LAYER || entity.Layer == REUSED_TILES_LAYER ||
                     entity.Layer == ANNOTATIONS_LAYER || entity.Layer == HATCH_LAYER ||
                     entity.Layer == OVERLAP_LAYER || entity.Layer == LBOOT_LAYER ||
                     entity.Layer == FACADE_LABELS_LAYER))
                {
                    objectsToDelete.Add(objId);
                }
            }

            // Удаляем объекты
            modelSpace.UpgradeOpen();
            foreach (var objId in objectsToDelete)
            {
                var entity = tr.GetObject(objId, OpenMode.ForWrite) as Entity;
                entity?.Erase();
            }
        }

        /// <summary>
        /// Создает легенду для чертежа
        /// </summary>
        public static void CreateLegend(Database db, Transaction tr, LayoutResult result)
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // Позиция для легенды (правый верхний угол чертежа)
            var legendStart = new Point3d(100, 100, 0);
            var lineHeight = 200; // 200 мм между строками

            // Заголовок
            var titleText = new MText
            {
                Location = legendStart,
                Contents = "ИНСУЛЯЦИЯ MASTER PRO - РЕЗУЛЬТАТЫ РАСКЛАДКИ",
                TextHeight = 100,
                Layer = ANNOTATIONS_LAYER,
                Attachment = AttachmentPoint.TopLeft
            };
            modelSpace.AppendEntity(titleText);
            tr.AddNewlyCreatedDBObject(titleText, true);

            // Статистика
            var currentY = legendStart.Y - lineHeight;
            
            var newTilesText = new MText
            {
                Location = new Point3d(legendStart.X, currentY, 0),
                Contents = $"Новые плиты: {result.NewTilesCount} шт. ({result.NewTilesArea / 1000000:F2} м²)",
                TextHeight = 60,
                Layer = ANNOTATIONS_LAYER,
                Attachment = AttachmentPoint.TopLeft
            };
            modelSpace.AppendEntity(newTilesText);
            tr.AddNewlyCreatedDBObject(newTilesText, true);

            currentY -= lineHeight;
            var reusedText = new MText
            {
                Location = new Point3d(legendStart.X, currentY, 0),
                Contents = $"Использовано остатков: {result.ReusedRemnantsCount} шт. ({result.ReusedArea / 1000000:F2} м²)",
                TextHeight = 60,
                Layer = ANNOTATIONS_LAYER,
                Attachment = AttachmentPoint.TopLeft
            };
            modelSpace.AppendEntity(reusedText);
            tr.AddNewlyCreatedDBObject(reusedText, true);

            currentY -= lineHeight;
            var totalText = new MText
            {
                Location = new Point3d(legendStart.X, currentY, 0),
                Contents = $"Общая площадь утепления: {result.TotalInsulationArea / 1000000:F2} м²",
                TextHeight = 60,
                Layer = ANNOTATIONS_LAYER,
                Attachment = AttachmentPoint.TopLeft
            };
            modelSpace.AppendEntity(totalText);
            tr.AddNewlyCreatedDBObject(totalText, true);
        }

        /// <summary>
        /// Визуальная маркировка фасадов и рядов (ТЗ п.5).
        /// Номер фасада: "1 фасад", "2 фасад" - высота 300, по центру X, на 400 мм выше верха.
        /// Номера рядов: "1", "2"... - высота 150, слева от фасада на 400 мм, нумерация снизу вверх.
        /// </summary>
        public static void VisualizeFacadeLabels(Database db, Transaction tr, List<Models.FacadeInfo> facadeInfos, List<TileInfo> allTiles)
        {
            if (facadeInfos == null || !facadeInfos.Any()) return;

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            foreach (var facade in facadeInfos)
            {
                var facadeLabelPos = new Point3d(
                    facade.CenterX,
                    facade.MaxY + 400,
                    0);

                var facadeLabel = new MText
                {
                    Location = facadeLabelPos,
                    Contents = $"\\W1.4;{facade.Index} фасад",
                    TextHeight = 500,
                    Layer = FACADE_LABELS_LAYER,
                    Attachment = AttachmentPoint.MiddleCenter,
                    ColorIndex = 2
                };
                modelSpace.AppendEntity(facadeLabel);
                tr.AddNewlyCreatedDBObject(facadeLabel, true);

                var facadeTiles = allTiles.Where(t => 
                    t.BlockNumber.StartsWith($"{facade.Index}.")).ToList();
                    
                var rowNumbers = facadeTiles
                    .GroupBy(t => t.RowIndex)
                    .Select(g => new { 
                        RowIndex = g.Key, 
                        CenterY = (g.Min(t => t.Position.Y) + g.Max(t => t.Position.Y + t.Height)) / 2.0 
                    })
                    .OrderBy(r => r.RowIndex)
                    .ToList();

                foreach (var row in rowNumbers)
                {
                    var rowLabelPos = new Point3d(
                        facade.MinX - 400,
                        row.CenterY,
                        0);

                    int displayNumber = row.RowIndex + 1;

                    var rowLabel = new MText
                    {
                        Location = rowLabelPos,
                        Contents = $"\\W1.4;{displayNumber}",
                        TextHeight = 250,
                        Layer = FACADE_LABELS_LAYER,
                        Attachment = AttachmentPoint.MiddleCenter,
                        ColorIndex = 2
                    };
                    modelSpace.AppendEntity(rowLabel);
                    tr.AddNewlyCreatedDBObject(rowLabel, true);
                }
            }
        }
    }
}
