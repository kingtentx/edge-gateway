namespace EdgeGateway.WinForms;

public sealed partial class MainForm
{
    private void OperationFeedbackClicked(object? sender, EventArgs e)
    {
        MessageBox.Show(this, _lastFeedback, "最近操作结果",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
