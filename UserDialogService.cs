using System;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;

namespace InsulationMasterPro.Services
{
    /// <summary>
    /// Сервис диалоговых окон. Настройка выпусков за контур (ТЗ v2.1).
    /// </summary>
    public static class UserDialogService
    {
        /// <summary>
        /// Выпуск начинается с первого ряда (true) или со второго (false).
        /// Сохраняется на текущую сессию раскладки.
        /// </summary>
        public static bool FirstRowWithRelease { get; set; } = true;

        /// <summary>
        /// Спрашивает: "1 ряд с выпуском?" Да — выпуск с 1-го ряда, Нет — со 2-го.
        /// Возвращает true при "Да", false при "Нет" или отмене.
        /// </summary>
        public static bool AskFirstRowWithRelease()
        {
            try
            {
                Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                Editor ed = doc?.Editor;
                if (ed == null)
                {
                    FirstRowWithRelease = true;
                    return true;
                }

                var result = MessageBox.Show(
                    "1 ряд с выпуском?\n\nДа — выпуск с первого ряда, затем чередование через ряд.\nНет — выпуск со второго ряда.",
                    "Insulation Master Pro — выпуск за контур",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);

                FirstRowWithRelease = result == DialogResult.Yes;
                return FirstRowWithRelease;
            }
            catch (Exception)
            {
                FirstRowWithRelease = true;
                return true;
            }
        }

        /// <summary>
        /// Возвращает, является ли ряд с заданным индексом (0-based) рядом с выпуском.
        /// </summary>
        public static bool IsRowWithRelease(int rowIndex)
        {
            return FirstRowWithRelease ? (rowIndex % 2 == 0) : (rowIndex % 2 == 1);
        }
    }
}
