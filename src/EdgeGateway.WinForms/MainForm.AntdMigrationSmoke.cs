namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private void SmokeAntdMigration()
    {
        foreach (var control in Descendants(this))
        {
            if (control is System.Windows.Forms.DataGridView or System.Windows.Forms.Label or System.Windows.Forms.Button
                or System.Windows.Forms.TextBoxBase or System.Windows.Forms.ComboBox or System.Windows.Forms.NumericUpDown
                or System.Windows.Forms.DateTimePicker or System.Windows.Forms.CheckBox or System.Windows.Forms.TabControl
                or System.Windows.Forms.PictureBox or System.Windows.Forms.ProgressBar)
                throw new InvalidOperationException("Native visual control remains: " + control.Name + "/" + control.GetType().FullName);
            if (control is GatewayTable table && table.AccessibilityObject.GetChildCount() != table.Rows.Count)
                throw new InvalidOperationException("Antd table accessibility rows are missing: " + table.Name);
        }
        foreach (var item in new[]
        {
            (MessageBoxButtons.OK, "确定", DialogResult.OK),
            (MessageBoxButtons.OKCancel, "取消", DialogResult.Cancel),
            (MessageBoxButtons.YesNo, "是", DialogResult.Yes),
            (MessageBoxButtons.YesNo, "否", DialogResult.No),
            (MessageBoxButtons.YesNo, "Enter", DialogResult.No),
            (MessageBoxButtons.YesNoCancel, "是", DialogResult.Yes),
            (MessageBoxButtons.YesNoCancel, "否", DialogResult.No),
            (MessageBoxButtons.YesNoCancel, "取消", DialogResult.Cancel),
            (MessageBoxButtons.YesNoCancel, "Escape", DialogResult.Cancel)
        })
        {
            Exception? failure = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 250 };
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan? observedAt = null;
            timer.Tick += (_, _) =>
            {
                var modal = System.Windows.Forms.Application.OpenForms.Cast<Form>()
                    .FirstOrDefault(form => form.GetType().FullName == "AntdUI.LayeredFormModal");
                if (modal is null)
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(8)) { timer.Stop(); failure = new InvalidOperationException("Official modal did not open."); }
                    return;
                }
                // Shown queues the default focus; a timer started before ShowDialog
                // can already be due while the HWND is still opening.
                if (observedAt is null) { observedAt = deadline.Elapsed; return; }
                if (deadline.Elapsed - observedAt.Value < TimeSpan.FromMilliseconds(250)) return;
                timer.Stop();
                try
                {
                    var buttons = Descendants(modal).OfType<AntdUI.Button>().ToArray();
                    if (DpiConfirmationSmoke) VerifyModalOrderForSmoke(modal, buttons, item.Item1, item.Item2);
                    if (item.Item1 == MessageBoxButtons.YesNo && item.Item2 == "否"
                        && modal.AcceptButton is not AntdUI.Button { Text: "否" })
                        throw new InvalidOperationException("Safe default must be No, not Yes.");
                    if (item.Item2 == "取消") SaveEditorScreenshot(modal, "antd-modal", item.Item1.ToString());
                    if (item.Item2 is "Escape" or "Enter")
                    {
                        var message = Message.Create(modal.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                        var method = typeof(Form).GetMethod("ProcessDialogKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                        method.Invoke(modal, [item.Item2 == "Escape" ? Keys.Escape : Keys.Enter]);
                    }
                    else buttons.Single(button => button.Text == item.Item2).PerformClick();
                }
                catch (Exception error) { failure = error; modal.Close(); }
            };
            timer.Start();
            var result = GatewayMessageBox.Show(this, "验证官方确认、取消和默认按钮行为。", "界面检查",
                item.Item1, MessageBoxIcon.Question,
                item.Item1 == MessageBoxButtons.YesNo && item.Item2 is "否" or "Enter" ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1);
            timer.Stop();
            if (failure is not null) throw failure;
            if (result != item.Item3) throw new InvalidOperationException($"Modal {item.Item1}/{item.Item2}: expected {item.Item3}, got {result}.");
        }
        Console.WriteLine("Antd migration smoke passed: no native visible-control classes in main tree; accessible table rows; official Modal OK/Yes/No/Cancel/Escape and safe default.");
    }
}
