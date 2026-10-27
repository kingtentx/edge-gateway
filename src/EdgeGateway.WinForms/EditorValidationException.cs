namespace EdgeGateway.WinForms;

/// <summary>A field-specific validation failure, preserving normal InvalidOperationException handling.</summary>
internal sealed class EditorValidationException(string message, Control field) : InvalidOperationException(message)
{
    public Control Field { get; } = field;
}
