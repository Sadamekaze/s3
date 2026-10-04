using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SuikodenText.Desktop.Helpers
{
    public static class AsyncWork
    {
        public static async Task<T> RunAsync<T>(
            Form owner,
            Control button,
            Func<T> work)
        {
            bool previousEnabled = button.Enabled;
            bool previousCursor = owner.UseWaitCursor;

            button.Enabled = false;
            owner.UseWaitCursor = true;

            try
            {
                return await Task.Run(work);
            }
            finally
            {
                if (!owner.IsDisposed)
                    owner.UseWaitCursor = previousCursor;

                if (!button.IsDisposed)
                    button.Enabled = previousEnabled;
            }
        }
    }
}