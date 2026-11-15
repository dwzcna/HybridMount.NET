using HybridMount.Commons.Settings;
using HybridMount.Infrastructure.Windows;
using HybridMount.Models.Localization;
using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace HybridMount.Infrastructure.Localization;

internal sealed class RegistryUILanguageStore(string path) : IUILanguageStore
{
    public string? Read() => WinRegisterService.ReadValue(path, "UILanguage") as string;
    public void Write(string choice)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, true) ?? throw new IOException("UILanguage store unavailable");
        key.SetValue("UILanguage", choice, RegistryValueKind.String);
    }
}
