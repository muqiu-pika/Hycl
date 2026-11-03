namespace Hycl.Launcher.Core.Models;

/// <summary>工具页的单个工具卡片（对应网页版 ToolsView 的 tools 数组）。</summary>
public sealed record ToolItemViewModel(string IconKey, string Title, string Description, string ButtonLabel);
