using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using InsulationMasterPro.Models;

namespace InsulationMasterPro.Data
{
    public class DatabaseBackup
    {
        public DateTime CreatedAt { get; set; }
        public List<Remnant> Remnants { get; set; } = new List<Remnant>();
        public int TotalCount => Remnants?.Count ?? 0;
        public double TotalArea => Remnants?.Sum(r => r.Area) ?? 0;
    }

    public static class RemnantDatabase
    {
        private static readonly string DbPath = @"C:\DB_INS\remnants.json";
        private static readonly string DbDir = @"C:\DB_INS";
        private static readonly string BackupDir = @"C:\DB_INS\backups";
        private const int MaxBackups = 10;

        /// <summary>
        /// Загружает остатки из базы данных
        /// </summary>
        public static List<Remnant> LoadRemnants()
        {
            try
            {
                if (!File.Exists(DbPath))
                {
                    return new List<Remnant>();
                }

                string json = File.ReadAllText(DbPath);
                var remnants = JsonConvert.DeserializeObject<List<Remnant>>(json);
                
                if (remnants == null)
                {
                    return new List<Remnant>();
                }

                var validRemnants = remnants.Where(r => 
                    r.Width > 0 && r.Height > 0 && 
                    r.Width <= 5000 && r.Height <= 5000 &&
                    !r.IsUsed
                ).ToList();

                // C7: Дедупликация по GUID
                int beforeDedup = validRemnants.Count;
                validRemnants = validRemnants
                    .GroupBy(r => r.Id)
                    .Select(g => g.First())
                    .ToList();
                if (validRemnants.Count < beforeDedup)
                    LogOperation($"Дубликаты GUID при загрузке: удалено {beforeDedup - validRemnants.Count}");

                return validRemnants;
            }
            catch (Exception ex)
            {
                // В случае ошибки повреждения файла, пробуем восстановить из бэкапа
                return RestoreFromBackup() ?? new List<Remnant>();
            }
        }

        /// <summary>
        /// Сохраняет остатки в базу данных
        /// </summary>
        public static void SaveRemnants(List<Remnant> remnants)
        {
            try
            {
                if (!Directory.Exists(DbDir))
                {
                    Directory.CreateDirectory(DbDir);
                }

                // Создаем бэкап перед сохранением
                CreateBackup();

                // Подготавливаем данные для сохранения
                var remnantsToSave = remnants.Where(r => 
                    r.Width > 0 && r.Height > 0 && 
                    r.Width <= 5000 && r.Height <= 5000
                ).ToList();

                // Сортируем по дате добавления (новые в конце)
                remnantsToSave = remnantsToSave.OrderBy(r => r.AddedDate).ToList();

                var settings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore,
                    DateFormatString = "yyyy-MM-ddTHH:mm:ss"
                };

                string json = JsonConvert.SerializeObject(remnantsToSave, settings);
                File.WriteAllText(DbPath, json);

                // Логируем операцию
                LogOperation($"Сохранено {remnantsToSave.Count} остатков");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка сохранения БД: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Пакетное добавление остатков — одно чтение, один бэкап, одна запись.
        /// FIX v3.1.0: заменяет цикл AddRemnant() для устранения N бэкапов за сеанс.
        /// </summary>
        public static void AddRemnants(List<Remnant> newRemnants)
        {
            if (newRemnants == null || newRemnants.Count == 0) return;

            try
            {
                var existing = LoadRemnants();
                var existingIds = new HashSet<Guid>(existing.Select(r => r.Id));
                int added = 0;

                foreach (var r in newRemnants)
                {
                    if (r == null || r.Width <= 0 || r.Height <= 0) continue;
                    if (!existingIds.Contains(r.Id))
                    {
                        existing.Add(r);
                        existingIds.Add(r.Id);
                        added++;
                    }
                }

                if (added > 0)
                    SaveRemnants(existing);

                LogOperation($"Пакетно добавлено {added} остатков (из {newRemnants.Count} переданных)");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка пакетного добавления остатков: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Добавляет один остаток в базу данных.
        /// Дубликат определяется только по GUID: два физических куска одного размера — разные объекты.
        /// Time-based антидубликат (±1 минута по размеру) удалён — он отбрасывал реальные куски.
        /// </summary>
        public static void AddRemnant(Remnant remnant)
        {
            if (remnant == null || remnant.Width <= 0 || remnant.Height <= 0)
                return;

            try
            {
                var remnants = LoadRemnants();

                // Защита от дубля только по GUID (уникальный идентификатор физического куска)
                if (remnants.Any(r => r.Id == remnant.Id))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"DEBUG [RemnantDatabase.AddRemnant] SKIPPED duplicate GUID id={remnant.Id}");
                    return;
                }

                remnants.Add(remnant);
                SaveRemnants(remnants);

                System.Diagnostics.Debug.WriteLine(
                    $"DEBUG [RemnantDatabase.AddRemnant] added id={remnant.Id} " +
                    $"{remnant.Width:F0}×{remnant.Height:F0} total_count={remnants.Count}");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка добавления остатка: {ex.Message}");
            }
        }

        /// <summary>
        /// Удаляет остаток по ID
        /// </summary>
        public static void RemoveRemnant(Guid id)
        {
            try
            {
                var remnants = LoadRemnants();
                var removed = remnants.RemoveAll(r => r.Id == id);
                
                if (removed > 0)
                {
                    SaveRemnants(remnants);
                    LogOperation($"Удален остаток с ID: {id}");
                }
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка удаления остатка: {ex.Message}");
            }
        }

        /// <summary>
        /// Отмечает остаток как использованный
        /// </summary>
        public static void MarkAsUsed(Guid id)
        {
            try
            {
                var remnants = LoadRemnants();
                var remnant = remnants.FirstOrDefault(r => r.Id == id);
                
                if (remnant != null)
                {
                    remnant.IsUsed = true;
                    SaveRemnants(remnants);
                    LogOperation($"Отмечен как использованный: {id}");
                }
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка отметки остатка: {ex.Message}");
            }
        }

        /// <summary>
        /// Пакетная пометка остатков как использованных + добавление новых — одно чтение, одна запись.
        /// </summary>
        public static void UpdateAfterLayout(HashSet<Guid> usedIds, List<Remnant> newRemnants)
        {
            if ((usedIds == null || usedIds.Count == 0) && (newRemnants == null || newRemnants.Count == 0))
                return;

            try
            {
                if (!Directory.Exists(DbDir))
                    Directory.CreateDirectory(DbDir);

                List<Remnant> all;
                try { all = File.Exists(DbPath)
                    ? JsonConvert.DeserializeObject<List<Remnant>>(File.ReadAllText(DbPath)) ?? new List<Remnant>()
                    : new List<Remnant>(); }
                catch { all = new List<Remnant>(); }

                int marked = 0;
                if (usedIds != null && usedIds.Count > 0)
                {
                    foreach (var r in all)
                    {
                        if (!r.IsUsed && usedIds.Contains(r.Id))
                        {
                            r.IsUsed = true;
                            marked++;
                        }
                    }
                }

                int added = 0;
                if (newRemnants != null && newRemnants.Count > 0)
                {
                    var existingIds = new HashSet<Guid>(all.Select(r => r.Id));
                    foreach (var r in newRemnants)
                    {
                        if (r == null || r.Width <= 0 || r.Height <= 0) continue;
                        if (!existingIds.Contains(r.Id))
                        {
                            all.Add(r);
                            existingIds.Add(r.Id);
                            added++;
                        }
                    }
                }

                if (marked > 0 || added > 0)
                    SaveRemnants(all);

                LogOperation($"UpdateAfterLayout: помечено использованных={marked}, добавлено новых={added}");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка UpdateAfterLayout: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Очищает склад — удаляет все остатки из базы данных (перед очисткой создаётся бэкап).
        /// </summary>
        public static void ClearAll()
        {
            try
            {
                SaveRemnants(new List<Remnant>());
                LogOperation("Склад очищен: все остатки удалены");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка очистки склада: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Публичный бэкап базы данных (для INS_STOCK_IMPORT и других операций замены).
        /// </summary>
        public static void BackupDatabase() => CreateBackup();

        /// <summary>
        /// Заменяет все неиспользованные остатки на новый список.
        /// Сохраняет историю (IsUsed=true) без изменений.
        /// </summary>
        public static void ReplaceUnusedRemnants(List<Remnant> newRemnants)
        {
            if (newRemnants == null) throw new ArgumentNullException(nameof(newRemnants));

            try
            {
                if (!Directory.Exists(DbDir))
                    Directory.CreateDirectory(DbDir);

                List<Remnant> all;
                try
                {
                    all = File.Exists(DbPath)
                        ? JsonConvert.DeserializeObject<List<Remnant>>(File.ReadAllText(DbPath)) ?? new List<Remnant>()
                        : new List<Remnant>();
                }
                catch { all = new List<Remnant>(); }

                var usedHistory = all.Where(r => r.IsUsed).ToList();
                int removedUnused = all.Count - usedHistory.Count;

                var result = new List<Remnant>(usedHistory);
                result.AddRange(newRemnants);

                SaveRemnants(result);
                LogOperation($"ReplaceUnusedRemnants: удалено {removedUnused} неисп., добавлено {newRemnants.Count} новых");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка ReplaceUnusedRemnants: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Очищает базу данных от старых и использованных остатков
        /// </summary>
        public static int CleanupDatabase(int maxAgeDays = 30)
        {
            try
            {
                var remnants = LoadRemnants();
                var cutoffDate = DateTime.Now.AddDays(-maxAgeDays);
                
                var initialCount = remnants.Count;
                
                // Удаляем старые и использованные остатки
                remnants.RemoveAll(r => r.IsUsed || r.AddedDate < cutoffDate);
                
                SaveRemnants(remnants);
                
                var removedCount = initialCount - remnants.Count;
                LogOperation($"Очистка БД: удалено {removedCount} остатков");
                
                return removedCount;
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка очистки БД: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Получает статистику базы данных
        /// </summary>
        public static DatabaseStatistics GetStatistics()
        {
            try
            {
                var remnants = LoadRemnants();
                
                if (!remnants.Any())
                {
                    return new DatabaseStatistics();
                }

                return new DatabaseStatistics
                {
                    TotalCount = remnants.Count,
                    TotalArea = remnants.Sum(r => r.Area),
                    AverageArea = remnants.Average(r => r.Area),
                    LargestRemnant = remnants.OrderByDescending(r => r.Area).FirstOrDefault(),
                    SmallestRemnant = remnants.OrderBy(r => r.Area).FirstOrDefault(),
                    OldestRemnant = remnants.OrderBy(r => r.AddedDate).FirstOrDefault(),
                    NewestRemnant = remnants.OrderByDescending(r => r.AddedDate).FirstOrDefault(),
                    LastModified = File.Exists(DbPath) ? File.GetLastWriteTime(DbPath) : DateTime.MinValue
                };
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка получения статистики: {ex.Message}");
                return new DatabaseStatistics();
            }
        }

        /// <summary>
        /// Создает резервную копию базы данных
        /// </summary>
        private static void CreateBackup()
        {
            try
            {
                if (!File.Exists(DbPath)) return;

                if (!Directory.Exists(BackupDir))
                {
                    Directory.CreateDirectory(BackupDir);
                }

                string backupFile = Path.Combine(BackupDir, $"remnants_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.Copy(DbPath, backupFile, overwrite: true);

                // Удаляем старые бэкапы
                var backupFiles = Directory.GetFiles(BackupDir, "remnants_backup_*.json")
                                      .OrderByDescending(f => f)
                                      .Skip(MaxBackups);

                foreach (var oldBackup in backupFiles)
                {
                    File.Delete(oldBackup);
                }

                LogOperation($"Создан бэкап: {Path.GetFileName(backupFile)}");
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка создания бэкапа: {ex.Message}");
            }
        }

        /// <summary>
        /// Восстанавливает базу данных из бэкапа
        /// </summary>
        private static List<Remnant> RestoreFromBackup()
        {
            try
            {
                if (!Directory.Exists(BackupDir))
                    return null;

                var backupFiles = Directory.GetFiles(BackupDir, "remnants_backup_*.json")
                                      .OrderByDescending(f => f);

                foreach (var backupFile in backupFiles)
                {
                    try
                    {
                        string json = File.ReadAllText(backupFile);
                        var remnants = JsonConvert.DeserializeObject<List<Remnant>>(json);
                        
                        if (remnants != null)
                        {
                            LogOperation($"БД восстановлена из бэкапа: {Path.GetFileName(backupFile)}");
                            return remnants;
                        }
                    }
                    catch
                    {
                        continue; // Пробуем следующий бэкап
                    }
                }
            }
            catch (Exception ex)
            {
                LogOperation($"Ошибка восстановления из бэкапа: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Логирует операции с базой данных
        /// </summary>
        private static void LogOperation(string message)
        {
            try
            {
                string logFile = Path.Combine(DbDir, "database.log");
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(logFile, logEntry + Environment.NewLine);
            }
            catch
            {
                // Игнорируем ошибки логирования
            }
        }
    }

    public class DatabaseStatistics
    {
        public int TotalCount { get; set; }
        public double TotalArea { get; set; }
        public double AverageArea { get; set; }
        public Remnant LargestRemnant { get; set; }
        public Remnant SmallestRemnant { get; set; }
        public Remnant OldestRemnant { get; set; }
        public Remnant NewestRemnant { get; set; }
        public DateTime LastModified { get; set; }

        public override string ToString()
        {
            return $"Всего: {TotalCount} шт. | Общая площадь: {TotalArea / 1000000:F2} м² | " +
                   $"Средняя: {AverageArea / 1000000:F3} м² | Последнее изменение: {LastModified:dd.MM.yyyy HH:mm}";
        }
    }

    /// <summary>
    /// DTO для одной строки из листа «Нач. склад» в Excel (INS_STOCK_IMPORT).
    /// </summary>
    public class RemnantImportRow
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Quantity { get; set; } = 1;
    }
}
