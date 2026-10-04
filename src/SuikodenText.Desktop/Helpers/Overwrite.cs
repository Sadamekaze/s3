using System;
using System.Windows.Forms;

namespace SuikodenText.Desktop.Helpers
{
    public static class Overwrite
    {
        public static Func<string, bool> ConfirmOverwrite(IWin32Window owner)
        {
            return path => MessageBox.Show(
                owner,
                $"File already exists:\n{path}\n\n" +
                "Yes: Overwrite\nNo: Save with an _edit suffix",
                "File already exists",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            ) == DialogResult.Yes;
        }

        public static Func<string, bool> CreateBatchConfirmation(
            IWin32Window owner)
        {
            bool? batchOverwrite = null;

            return path =>
            {
                if (batchOverwrite.HasValue)
                    return batchOverwrite.Value;

                batchOverwrite = MessageBox.Show(
                    owner,
                    $"File already exists:\n{path}\n\n" +
                    "This choice will apply to all existing files " +
                    "in this batch.\n\n" +
                    "Yes: Overwrite\nNo: Save with an _edit suffix",
                    "Batch overwrite",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                ) == DialogResult.Yes;

                return batchOverwrite.Value;
            };
        }
    }
}