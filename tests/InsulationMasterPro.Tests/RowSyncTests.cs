using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using InsulationMasterPro.OrTools;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.Tests
{
    public class RowSyncTests
    {
        [Fact]
        public void DivideIntoRows_AppliesSyncedHeights_WhenNoConflicts()
        {
            // Arrange
            var constraints = new OptimizationConstraints
            {
                TileWidth = 1200,
                TileHeight = 600,
                MinRowHeight = 300,
                MinBlock = 200,
                MinBlockNearWindow = 150,
                WindowOverlap = 20,
                MinJointOffset = 100
            };

            var syncedHeights = new List<double> { 600, 400, 600 };
            var windows = new List<WindowInfo>(); // No windows

            // Act
            var rows = Preprocessor.DivideIntoRows(
                facadeWidth: 3000,
                facadeHeight: 1600,
                minY: 0,
                tileHeight: 600,
                minRowHeight: 300,
                firstRowWithRelease: false,
                windows: windows,
                stockRemnants: new List<Remnant>(),
                constraints: constraints,
                syncedRowHeights: syncedHeights);

            // Assert
            Assert.Equal(3, rows.Count);
            Assert.Equal(600, rows[0].Height);
            Assert.Equal(400, rows[1].Height);
            Assert.Equal(600, rows[2].Height);
        }

        [Fact]
        public void DivideIntoRows_FallsBack_WhenSyncedHeightsConflictWithWindows()
        {
            // Arrange
            var constraints = new OptimizationConstraints
            {
                TileWidth = 1200,
                TileHeight = 600,
                MinRowHeight = 300,
                MinBlock = 200,
                MinBlockNearWindow = 150,
                WindowOverlap = 20,
                MinJointOffset = 100
            };

            var syncedHeights = new List<double> { 600, 400, 600 };
            // Conflict window: Y=0..600 vs boundaries
            // A row boundary is at Y=600. Window goes from 400 to 1000.
            // safeMargin is 130, so conflict zone is 400+130=530 to 1000-130=870.
            // 600 is inside [530, 870] so it will raise a conflict.
            var windows = new List<WindowInfo>
            {
                new WindowInfo { Id = "w1", MinX = 1000, MaxX = 2000, MinY = 400, MaxY = 1000 }
            };

            // Act
            var rows = Preprocessor.DivideIntoRows(
                facadeWidth: 3000,
                facadeHeight: 1600,
                minY: 0,
                tileHeight: 600,
                minRowHeight: 300,
                firstRowWithRelease: false,
                windows: windows,
                stockRemnants: new List<Remnant>(),
                constraints: constraints,
                syncedRowHeights: syncedHeights);

            // Assert
            // Because of the conflict, the grid should be recalculated by RowHeightForecaster
            // If RowHeightForecaster runs, it will likely output a different grid.
            // Let's just check that it does not match exactly the conflicting synced grid.
            var rowHeights = rows.Select(r => r.Height).ToList();
            bool isIdentical = rowHeights.Count == 3 && rowHeights[0] == 600 && rowHeights[1] == 400 && rowHeights[2] == 600;
            Assert.False(isIdentical, "Grid should be recalculated, but it was identical.");
        }
    }
}
