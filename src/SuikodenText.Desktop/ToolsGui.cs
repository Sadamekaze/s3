using System.Security.AccessControl;
using SuikodenText.Core;
using SuikodenText.Desktop.Helpers;

namespace SuikodenText.Desktop
{
    public partial class ToolGui : Form
    {
        public enum InputMode
        {
            SingleFile,
            Folder
        }

        public ToolGui()
        {
            InitializeComponent();
            CenterSadamekazeLabel();

            SelectMethod.DataSource = Enum.GetValues<InputMode>();
            SelectMethod.SelectedItem = InputMode.SingleFile;
        }

        private void CenterSadamekazeLabel()
        {
            SadamekazeLabel.Left =
                (ClientSize.Width - SadamekazeLabel.Width) / 2;

            SadamekazeLabel.Top =
                Export.Top + (Export.Height - SadamekazeLabel.Height) / 2;
        }

        private async void Export_Click(object sender, EventArgs e)
        {
            ArchiveState state;

            string binFilePath;
            string? fsectFilePath = null;
            string? elfFilepath = null;

            string binFolderPath;
            string jsonOutputPath;
            string folderOutputPath;

            switch (SelectMethod.SelectedItem)
            {
                case InputMode.SingleFile:
                    binFilePath = DialogOpen.FileOpenBin();
                    if (string.IsNullOrWhiteSpace(binFilePath)) return;

                    if (binFilePath.ToLowerInvariant().Contains("etc.bin"))
                    {
                        elfFilepath = DialogOpen.FileOpenElf();
                        if (string.IsNullOrWhiteSpace(elfFilepath)) return;
                        state = await ArchiveLoader.LoadAsync(binFilePath, exe: elfFilepath);

                    }
                    else
                    {
                        fsectFilePath = DialogOpen.FileOpenFsect();
                        if (string.IsNullOrWhiteSpace(fsectFilePath)) return;
                        state = await ArchiveLoader.LoadAsync(binFilePath, fsect: fsectFilePath);                    
                    }

                    jsonOutputPath = DialogOpen.FileSaveJson();
                    if(string.IsNullOrWhiteSpace(jsonOutputPath)) return;
                    var doc = ArchiveReader.Export(state); Files.WriteJson(jsonOutputPath, doc, overwrite: Overwrite.ConfirmOverwrite(this));

                    break;
                case InputMode.Folder:
                    binFolderPath = DialogOpen.FolderOpen("Select BIN folder");
                    if (string.IsNullOrWhiteSpace(binFolderPath)) return;

                    if (File.Exists(Path.Combine(binFolderPath, "etc.bin")))
                    {
                        elfFilepath = DialogOpen.FileOpenElf();
                        if(string.IsNullOrWhiteSpace(elfFilepath)) return;
                    }

                    bool onlyEtcBin = Directory.GetFiles(binFolderPath, "*.bin").Length == 1 && File.Exists(Path.Combine(binFolderPath, "etc.bin"));

                    if (!onlyEtcBin)
                    {
                        fsectFilePath = DialogOpen.FileOpenFsect();
                        if (string.IsNullOrWhiteSpace(fsectFilePath)) return;
                    }
                    
                    folderOutputPath = DialogOpen.FolderOpen("Select output folder");
                    if(string.IsNullOrWhiteSpace(folderOutputPath)) return;
                    Batch.ExportFolder(binFolderPath, fsect: fsectFilePath, folderOutputPath, exe: elfFilepath, overwrite: Overwrite.ConfirmOverwrite(this));

                    break;
            }
        }

        private void Import_Click(object sender, EventArgs e)
        {
            ArchiveState state;

            string binFilePath;
            string? fsectFilePath = null;
            string? elfFilepath = null ;
            string jsonFilePath;

            string jsonFolderPath;
            string binFolderPath;
            string folderOutputPath;

            switch (SelectMethod.SelectedItem)
            {
                case InputMode.SingleFile:
                    jsonFilePath = DialogOpen.FileOpenJson();
                    if(string.IsNullOrWhiteSpace(jsonFilePath)) return;

                    binFilePath = DialogOpen.FileOpenBin();
                    if(string.IsNullOrWhiteSpace(binFilePath)) return;

                    if(binFilePath.ToLowerInvariant().Contains("etc.bin"))
                    {
                        elfFilepath = DialogOpen.FileOpenElf();
                        if (string.IsNullOrWhiteSpace(elfFilepath)) return;
                        state = ArchiveReader.Load(binFilePath, exe: elfFilepath);
                    }
                    else
                    {
                        fsectFilePath = DialogOpen.FileOpenFsect();
                        if (string.IsNullOrWhiteSpace(fsectFilePath)) return;
                        state = ArchiveReader.Load(binFilePath, fsect: fsectFilePath);
                    }

                    folderOutputPath = DialogOpen.FolderOpen("Select output folder");
                    if(string.IsNullOrWhiteSpace(folderOutputPath)) return;
                    var report = ArchiveWriter.Import(state, Files.ReadDocument(jsonFilePath), folderOutputPath, overwrite: Overwrite.ConfirmOverwrite(this));

                    break;
                case InputMode.Folder:
                    jsonFolderPath = DialogOpen.FolderOpen("Select JSON folder");
                    if (string.IsNullOrWhiteSpace(jsonFolderPath)) return;

                    binFolderPath = DialogOpen.FolderOpen("Select BIN folder");
                    if (string.IsNullOrWhiteSpace(binFolderPath)) return;

                    if (File.Exists(Path.Combine(jsonFolderPath, "etc.json")))
                    {
                        elfFilepath = DialogOpen.FileOpenElf();
                        if (string.IsNullOrWhiteSpace(elfFilepath)) return;
                    }

                    bool onlyEtcBin = Directory.GetFiles(jsonFolderPath, "*.json").Length == 1 && File.Exists(Path.Combine(jsonFolderPath, "etc.json"));

                    if (!onlyEtcBin)
                    {
                        fsectFilePath = DialogOpen.FileOpenFsect();
                        if (string.IsNullOrWhiteSpace(fsectFilePath)) return;
                    }

                    folderOutputPath = DialogOpen.FolderOpen("Select output folder");
                    if (string.IsNullOrWhiteSpace(folderOutputPath)) return;
                    Batch.ImportFolder(binFolderPath, fsect: fsectFilePath, jsonFolderPath, folderOutputPath, exe: elfFilepath, overwrite: Overwrite.ConfirmOverwrite(this));

                    break;
            }
        }

        private void HandleMouseEnter(object sender, EventArgs e)
        {
            Font hoverFont = new Font(
                "Arial", 14F,
                FontStyle.Bold | FontStyle.Italic | FontStyle.Underline
            );

            if (sender is Control c)
                c.Font = hoverFont;
        }

        private void HandleMouseLeave(object sender, EventArgs e)
        {
            Font normalFont = new Font("Arial", 14F, FontStyle.Bold);

            if (sender is Control c)
                c.Font = normalFont;
        }
    }
}
