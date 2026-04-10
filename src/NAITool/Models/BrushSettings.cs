namespace NAITool.Models;

/// <summary>
/// 笔刷设置。直接属性访问，不使用 MVVM 绑定。
/// </summary>
public class BrushSettings
{
    public float BrushSize { get; set; } = 20f;
    public StrokeTool CurrentTool { get; set; } = StrokeTool.Brush;
    public float BrushOpacity { get; set; } = 1.0f;
    public float BrushRadius => BrushSize / 2f;
}
