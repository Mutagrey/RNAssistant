using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RNAssistant.DeploymentKeys
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new IssuerForm());
        }
    }
    internal sealed class IssuerForm : Form
    {
        private readonly TextBox _repository = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox _password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly TextBox _confirmation = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly CheckBox _replace = new CheckBox { AutoSize = true, Text = "Заменить существующие ключи (с резервной копией)" };
        private readonly Button _choose = new Button { Text = "Выбрать…", AutoSize = true };
        private readonly Button _generate = new Button { Text = "Создать ключи", AutoSize = true };
        private readonly TextBox _status = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
        private bool _busy;

        internal IssuerForm()
        {
            Text = "RNAssistant — ключи разработчика";
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(680, 470); MinimumSize = new Size(540, 440);
            var form = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 8 };
            form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (var i = 0; i < 7; i++) form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var note = new Label { Text = "Разовая настройка. После создания или замены ключей пересоберите приложение. Пароль также используется для входа в режим разработчика.", AutoSize = true, MaximumSize = new Size(610, 0), Margin = new Padding(0, 0, 0, 12) };
            form.Controls.Add(note, 0, 0); form.SetColumnSpan(note, 2);
            form.Controls.Add(new Label { Text = "Папка проекта RNAssistant", AutoSize = true }, 0, 1);
            form.Controls.Add(_repository, 0, 2); form.Controls.Add(_choose, 1, 2);
            form.Controls.Add(PasswordField("Парольная фраза (от 12 символов)", _password), 0, 3); form.SetColumnSpan(form.GetControlFromPosition(0, 3), 2);
            form.Controls.Add(PasswordField("Повторите пароль", _confirmation), 0, 4); form.SetColumnSpan(form.GetControlFromPosition(0, 4), 2);
            form.Controls.Add(_replace, 0, 5); form.SetColumnSpan(_replace, 2);
            form.Controls.Add(_generate, 0, 6);
            form.Controls.Add(_status, 0, 7); form.SetColumnSpan(_status, 2);
            Controls.Add(form); AcceptButton = _generate;
            _choose.Click += (sender, args) => {
                using (var dialog = new FolderBrowserDialog { Description = "Корень исходников RNAssistant", ShowNewFolderButton = false })
                    if (dialog.ShowDialog(this) == DialogResult.OK) _repository.Text = dialog.SelectedPath;
            };
            _generate.Click += Generate;
            FormClosing += (sender, args) => { if (_busy) args.Cancel = true; };
        }
        private static Control PasswordField(string label, TextBox input)
        {
            var field = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 8, 0, 4) };
            field.Controls.Add(new Label { Text = label, AutoSize = true }); field.Controls.Add(input);
            return field;
        }
        private async void Generate(object sender, EventArgs args)
        {
            if (_busy) return;
            if (_password.Text != _confirmation.Text) { _status.Text = "Пароли не совпадают."; return; }
            var password = _password.Text; var repository = _repository.Text.Trim(); var replace = _replace.Checked;
            _busy = true;
            foreach (var control in new Control[] { _repository, _password, _confirmation, _replace, _choose, _generate }) control.Enabled = false;
            _status.Text = "Создание и проверка ключей…";
            try
            {
                var result = await Task.Run(() => IssuerFiles.Generate(repository, password, replace));
                _status.Text = "Готово. Публичные данные для следующей сборки:\r\n" + result.PublicFile +
                    "\r\n\r\nЗакрытый ключ — только для вас:\r\n" + result.PrivateFile +
                    "\r\n\r\nВ приложении введите пароль и путь к этому .bin. Пользователям передавайте только deployment-config.bin.";
            }
            catch (Exception error) { _status.Text = error.Message; }
            finally
            {
                password = null; _password.Clear(); _confirmation.Clear(); _busy = false;
                foreach (var control in new Control[] { _repository, _password, _confirmation, _replace, _choose, _generate }) control.Enabled = true;
            }
        }
    }
}
