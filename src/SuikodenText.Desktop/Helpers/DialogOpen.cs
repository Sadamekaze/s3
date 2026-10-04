using SuikodenText.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SuikodenText.Desktop.Helpers
{
    static class DialogOpen
    {

        public static string FileOpenBin()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "BIN Files (*.bin)|*.bin";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    if (string.Equals(
                    Path.GetFileName(dialog.FileName),
                    "FSECT.BIN",
                    StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("FSECT.BIN tidak dapat dipilih.");
                        return string.Empty;
                    }

                    return dialog.FileName;
                }
            }
            return string.Empty;
        }

        public static string FileOpenFsect()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "FSECT Files (fsect.bin)|fsect.bin";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.FileName;
                }
            }
            return string.Empty;
        }

        public static string FileOpenJson()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "JSON File (*.json)|*.json";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.FileName;
                }
            }

            return string.Empty;
        }

        public static string FileSaveJson() 
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "JSON File (*.json)|*.json";
                dialog.DefaultExt = "json";
                dialog.AddExtension = true;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.FileName;
                }
            }

            return string.Empty;
        }

        public static string FileOpenElf()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "slus_203.87 Files|slus_203.87";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.FileName;
                }
            }
            return string.Empty;
        }

        public static string FolderOpen(string description)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.UseDescriptionForTitle = true;

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    return dialog.SelectedPath;
                }
            }

            return string.Empty;
        }
    }
}
