namespace CxAgent.Core.Llm;

/// <summary>
/// How a model is named on screen. ONE PLACE, used by the status bar and by every place a child's
/// provider is shown, so a child running where the session runs reads exactly as the status bar does.
/// </summary>
public static class ModelNames
{
    private static readonly string[] ModelFileExtensions = [".gguf", ".safetensors", ".bin"];

    /// <summary>A model id without a model-file extension (`qwen….gguf` → `qwen…`). Nothing else is cut:
    /// a vendor prefix (`deepseek/…`) is part of what the model is called.</summary>
    public static string Short(string id)
    {
        foreach (var ext in ModelFileExtensions)
            if (id.Length > ext.Length && id.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return id[..^ext.Length];
        return id;
    }

    /// <summary>A `name:model` label with its model part shortened; a label with no name is shortened whole.</summary>
    public static string ShortLabel(string label) =>
        label.IndexOf(':') is var c and >= 0 ? label[..(c + 1)] + Short(label[(c + 1)..]) : Short(label);
}
