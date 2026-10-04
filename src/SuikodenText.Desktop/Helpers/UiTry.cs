using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SuikodenText.Desktop.Helpers
{
    public static class UiTry
    {
        public static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidDataException ex)
            {
                ShowError(ex.Message);
            }
        }

        public static T? Run<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (InvalidDataException ex)
            {
                ShowError(ex.Message);
                return default;
            }
        }

        public static async Task RunAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (InvalidDataException ex)
            {
                ShowError(ex.Message);
            }
        }

        public static async Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return await action();
            }
            catch (InvalidDataException ex)
            {
                ShowError(ex.Message);
                throw;
            }
        }

        private static void ShowError(string message)
        {
            MessageBox.Show(
                message,
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}