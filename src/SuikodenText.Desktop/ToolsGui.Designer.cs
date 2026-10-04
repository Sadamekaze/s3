using SuikodenText.Desktop.Helpers;

namespace SuikodenText.Desktop
{
    partial class ToolGui
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(ToolGui));
            Export = new Button();
            Import = new Button();
            SadamekazeLabel = new CustomLabel();
            SelectMethod = new ComboBox();
            SuspendLayout();
            // 
            // Export
            // 
            Export.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Export.Font = new Font("Arial", 8.25F);
            Export.Location = new Point(17, 143);
            Export.Name = "Export";
            Export.Size = new Size(73, 26);
            Export.TabIndex = 0;
            Export.Text = "Export";
            Export.UseVisualStyleBackColor = true;
            Export.Click += Export_Click;
            // 
            // Import
            // 
            Import.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Import.Font = new Font("Arial", 8.25F);
            Import.Location = new Point(374, 143);
            Import.Name = "Import";
            Import.Size = new Size(73, 26);
            Import.TabIndex = 1;
            Import.Text = "Import";
            Import.UseVisualStyleBackColor = true;
            Import.Click += Import_Click;
            // 
            // SadamekazeLabel
            // 
            SadamekazeLabel.AutoSize = true;
            SadamekazeLabel.BackColor = Color.Transparent;
            SadamekazeLabel.Font = new Font("Arial", 14F, FontStyle.Bold);
            SadamekazeLabel.ForeColor = SystemColors.ControlLightLight;
            SadamekazeLabel.Location = new Point(149, 145);
            SadamekazeLabel.Margin = new Padding(0);
            SadamekazeLabel.Name = "SadamekazeLabel";
            SadamekazeLabel.OutlineForeColor = Color.Black;
            SadamekazeLabel.OutlineWidth = 2.9F;
            SadamekazeLabel.Size = new Size(176, 24);
            SadamekazeLabel.TabIndex = 2;
            SadamekazeLabel.Text = "Sadamekaze (2026)";
            SadamekazeLabel.MouseEnter += HandleMouseEnter;
            SadamekazeLabel.MouseLeave += HandleMouseLeave;
            // 
            // SelectMethod
            // 
            SelectMethod.DropDownStyle = ComboBoxStyle.DropDownList;
            SelectMethod.FormattingEnabled = true;
            SelectMethod.Location = new Point(282, 17);
            SelectMethod.Margin = new Padding(8);
            SelectMethod.Name = "SelectMethod";
            SelectMethod.Size = new Size(165, 23);
            SelectMethod.TabIndex = 3;
            // 
            // ToolGui
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackgroundImage = (Image)resources.GetObject("$this.BackgroundImage");
            BackgroundImageLayout = ImageLayout.Stretch;
            ClientSize = new Size(464, 186);
            Controls.Add(SelectMethod);
            Controls.Add(SadamekazeLabel);
            Controls.Add(Import);
            Controls.Add(Export);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Name = "ToolGui";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Suikoden Text Tool";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button Export;
        private Button Import;
        private CustomLabel SadamekazeLabel;
        private ComboBox SelectMethod;
    }
}
