using HybridMount.Models.Localization;
using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
namespace HybridMount.Commons.Settings;

/// <summary>当前运行模式的语言偏好；写入失败不得发布新语言。</summary>
public interface IUILanguageStore { string? Read(); void Write(string choice); }
