using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Build.Framework;

/// <summary>由MSBuild同步核验资源源码及可选包内资源；仅访问指定本地文件，失败记录构建错误，不生成产品映像。</summary>
public sealed class HybridMountValidateResources : Microsoft.Build.Utilities.Task
{
    /// <summary>仓库绝对目录。</summary>
    [Required] public string RepositoryRoot { get; set; }
    /// <summary>包检查已生成的PRI XML转储；普通构建留空。</summary>
    public string PriDumpPath { get; set; }
    /// <summary>解包后的MUI目录；普通构建留空。</summary>
    public string MuiRoot { get; set; }
    /// <summary>包内原生模块预期架构，支持x64及ARM64。</summary>
    public string Platform { get; set; }
    private static readonly string[] Languages = { "zh-CN", "en-US", "zh-HK" };
    private static readonly string[] NativeTexts = { "HybridMount.NET", "释放空间", "始终保留在此设备上", "查看同步问题", "在应用中定位此文件或目录的同步问题；不会自动上传。", "管理此挂载的离线缓存；本地修改会受到保护。", "同步问题", "上传排除" };
    /// <summary>只映射资源目录中的源码文案；保持现有JSON文件格式。</summary>
    [DataContract]
    private sealed class CatalogItem
    {
        /// <summary>资源原始文案，用于核对翻译占位参数。</summary>
        [DataMember(Name = "Source", IsRequired = true)] public string Source { get; set; }
    }
    /// <summary>在调用构建线程执行；所有检查失败均返回false，阻止不完整资源进入构建／交付。</summary>
    public override bool Execute()
    {
        try
        {
            var root = Path.GetFullPath(RepositoryRoot);
            Dictionary<string, CatalogItem> catalog;
            using (var stream = File.OpenRead(Path.Combine(root, "HybridMount", "Resources", "UiCatalog.json")))
                catalog = (Dictionary<string, CatalogItem>)new DataContractJsonSerializer(typeof(Dictionary<string, CatalogItem>), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true }).ReadObject(stream);
            var texts = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var language in Languages)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlElement entry in ReadXml(Path.Combine(root, "HybridMount", "Resources", language, "Strings.resw")).SelectNodes("/root/data"))
                    values.Add(entry.GetAttribute("name"), entry.SelectSingleNode("value").InnerText);
                foreach (var key in values.Keys)
                    if (key.IndexOf('.') >= 0 && values.ContainsKey(key.Substring(0, key.IndexOf('.'))))
                        throw new InvalidDataException("resource prefix collision: " + language + "/" + key);
                if (texts.Count > 0 && !values.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).SequenceEqual(texts[Languages[0]].Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("resource key set drift: " + language);
                if (texts.Count > 0) foreach (var key in values.Keys)
                    if (Parameters(values[key]) != Parameters(texts[Languages[0]][key])) throw new InvalidDataException("resource parameters: " + language + "/" + key);
                foreach (var item in catalog)
                {
                    string value;
                    if (!values.TryGetValue(item.Key, out value) || Parameters(value) != Parameters(item.Value.Source))
                        throw new InvalidDataException("resource key/parameters: " + language + "/" + item.Key);
                }
                texts.Add(language, values);
            }
            foreach (var file in SourceFiles(Path.Combine(root, "HybridMount")))
                foreach (Match match in Regex.Matches(File.ReadAllText(file), "Text_[A-F0-9]{12}"))
                    if (!catalog.ContainsKey(match.Value)) throw new InvalidDataException("unknown UI resource: " + match.Value);
            ValidateXamlAndManifest(root, texts[Languages[0]]);
            foreach (var language in Languages)
            {
                var rc = File.ReadAllText(Path.Combine(root, "HybridMount.Ext", "Resources", language, "NativeStrings.rc"), Encoding.UTF8);
                for (var index = 0; index < NativeTexts.Length; index++)
                {
                    var expected = index == 0 ? NativeTexts[index] : texts[language][ResourceKey(NativeTexts[index])];
                    if (!rc.Contains("    " + (1001 + index) + " \"" + expected + "\"")) throw new InvalidDataException("MUI source drift: " + language + "/" + index);
                }
            }
            if (!String.IsNullOrEmpty(PriDumpPath)) ValidatePri(texts, ReadXml(PriDumpPath));
            if (!String.IsNullOrEmpty(MuiRoot)) ValidateMui(texts);
            Log.LogMessage(MessageImportance.High, "Localization checked: {0} keys x 3 languages / 8 native strings x 3", texts[Languages[0]].Count);
            return true;
        }
        catch (Exception error) { Log.LogErrorFromException(error, false); return false; }
    }
    /// <summary>跳过输出和外部重解析目录，只枚举仓库自有C#与XAML文件。</summary>
    private static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(folder))
                if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".xaml", StringComparison.OrdinalIgnoreCase)) yield return file;
            foreach (var directory in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(directory);
                if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
            }
        }
    }
    /// <summary>读取本地资源XML，禁止DTD及外部实体。</summary>
    private static XmlDocument ReadXml(string path)
    {
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
        return document;
    }
    /// <summary>保持既有占位参数的排序及重复次数契约。</summary>
    private static string Parameters(string text)
    {
        return String.Join(",", Regex.Matches(text.Replace("{{", "").Replace("}}", ""), @"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}").Cast<Match>().Select(match => match.Groups[1].Value).OrderBy(value => value, StringComparer.Ordinal));
    }
    /// <summary>计算与应用相同的UTF8 SHA-256前12位资源键，并释放算法对象。</summary>
    private static string ResourceKey(string text)
    {
        using (var sha = SHA256.Create()) return "Text_" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").Substring(0, 12);
    }
    /// <summary>核验包内实际PRI转储的三语言候选，缺失、重复或文案漂移均失败。</summary>
    private static void ValidatePri(Dictionary<string, Dictionary<string, string>> texts, XmlDocument dump)
    {
        var resources = dump.SelectNodes("//*[local-name()='NamedResource']").Cast<XmlElement>().ToArray();
        foreach (var key in texts[Languages[0]].Keys)
        {
            var suffix = "/Strings/" + ToPriPath(key);
            var matches = resources.Where(node => node.GetAttribute("uri").EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("PRI resource missing/duplicate: " + key);
            var candidates = matches[0].SelectNodes("*[local-name()='Candidate']").Cast<XmlElement>().ToArray();
            foreach (var language in Languages)
            {
                var selected = candidates.Where(candidate => Regex.IsMatch(candidate.GetAttribute("qualifiers"), "Language-" + Regex.Escape(language) + "(?:;|$)", RegexOptions.IgnoreCase)).ToArray();
                if (selected.Length != 1 || selected[0].SelectSingleNode("*[local-name()='Value']").InnerText != texts[language][key]) throw new InvalidDataException("PRI language missing/drift: " + language + "/" + key);
            }
        }
    }
    /// <summary>资源属性分隔点映射成PRI路径；using命名空间内的点保持。</summary>
    private static string ToPriPath(string key)
    {
        var result = new StringBuilder(); var depth = 0;
        foreach (var c in key) { if (c == '[') depth++; else if (c == ']') depth--; result.Append(c == '.' && depth == 0 ? '/' : c); }
        return result.ToString();
    }
    /// <summary>核对UID登记、控件字符串属性、编辑草稿保护及清单资源引用；不加载WinUI或访问包身份。</summary>
    private static void ValidateXamlAndManifest(string root, Dictionary<string, string> values)
    {
        const string x = "http://schemas.microsoft.com/winfx/2006/xaml", ui = "using:HybridMount.Helpers.UI";
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) {
            {"TextBlock", "Text"}, {"Run", "Text"}, {"MenuFlyoutItem", "Text"},
            {"AppBarButton", "Label"}, {"AppBarToggleButton", "Label"},
            {"Button", "Content"}, {"CheckBox", "Content"}, {"ComboBoxItem", "Content"}, {"NavigationViewItem", "Content"},
            {"ComboBox", "Header PlaceholderText"}, {"Expander", "Header"}, {"NumberBox", "Header PlaceholderText"},
            {"PasswordBox", "Header PlaceholderText"}, {"TextBox", "Header PlaceholderText"}, {"SettingRow", "Header Description"},
            {"CalendarDatePicker", "PlaceholderText"}, {"DatePicker", "Header"}, {"InfoBar", "Title Message"},
            {"ContentDialog", "Title PrimaryButtonText SecondaryButtonText CloseButtonText"}, {"ToggleSwitch", "Header OnContent OffContent"}
        };
        foreach (var file in SourceFiles(Path.Combine(root, "HybridMount")).Where(file => Path.GetExtension(file) == ".xaml"))
        {
            var document = ReadXml(file);
            foreach (XmlElement node in document.SelectNodes("//*"))
            {
                var uid = node.GetAttribute("Uid", x); var registered = node.GetAttribute("Ui.ResourceId", ui);
                if (uid != registered) throw new InvalidDataException("x:Uid/ResourceId mismatch: " + file + "/" + uid);
                if (uid.Length == 0) continue;
                var resourceId = uid.StartsWith("/Strings/", StringComparison.Ordinal) ? uid.Substring(9) : uid;
                var keys = values.Keys.Where(key => key.StartsWith(resourceId + ".", StringComparison.Ordinal)).ToArray();
                if (keys.Length == 0) throw new InvalidDataException("x:Uid resource missing: " + uid);
                foreach (var key in keys)
                {
                    var property = key.Substring(resourceId.Length + 1);
                    if (property == "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name" || property == "[using:Microsoft.UI.Xaml.Automation]AutomationProperties.HelpText" || property == "[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip") continue;
                    string allowed;
                    if (!properties.TryGetValue(node.LocalName, out allowed) || !allowed.Split(' ').Contains(property))
                        throw new InvalidDataException("x:Uid property/type mismatch: " + node.LocalName + "/" + key);
                    if (node.HasAttribute(property)) throw new InvalidDataException("x:Uid property has duplicate local value: " + key);
                }
                if (node.Attributes.Cast<XmlAttribute>().Any(attr => attr.LocalName.StartsWith("Ui.") && attr.LocalName.EndsWith("Key")))
                    throw new InvalidDataException("legacy static XAML key remains: " + uid);
            }
        }
        foreach (XmlElement node in ReadXml(Path.Combine(root, "HybridMount", "Package.appxmanifest")).SelectNodes("//*"))
        {
            foreach (var text in node.Attributes.Cast<XmlAttribute>().Select(attr => attr.Value).Concat(node.ChildNodes.Count == 1 && node.FirstChild is XmlText ? new[] { node.InnerText } : new string[0]))
                if (text.StartsWith("ms-resource:", StringComparison.Ordinal) && !values.ContainsKey(text.Substring("ms-resource:".Length).Replace("///Strings/", ""))) throw new InvalidDataException("manifest resource missing: " + text);
        }
    }
    /// <summary>仅以数据资源方式加载MUI，核验架构、无CLR及实际字符串；始终归还本次模块句柄。</summary>
    private void ValidateMui(Dictionary<string, Dictionary<string, string>> texts)
    {
        if (Platform != "x64" && Platform != "ARM64") throw new InvalidDataException("Unsupported MUI platform: " + Platform);
        var neutralPath = Path.Combine(MuiRoot, "HybridMount.Ext.resources.dll");
        var neutral = ReadMuiInfo(neutralPath, 2);
        ValidateDataImage(neutralPath, Platform == "ARM64" ? 0xaa64 : 0x8664, false);
        var languageOffset = (int)BitConverter.ToUInt32(neutral, 44);
        if (languageOffset <= 0 || Encoding.Unicode.GetString(neutral, languageOffset, neutral.Length - languageOffset).Split('\0')[0] != "zh-CN") throw new InvalidDataException("MUI ultimate fallback is not zh-CN");
        foreach (var language in Languages)
        {
            var path = Path.Combine(MuiRoot, language, "HybridMount.Ext.resources.dll.mui");
            var metadata = ReadMuiInfo(path, 4);
            if (!metadata.Skip(12).Take(16).SequenceEqual(neutral.Skip(12).Take(16))) throw new InvalidDataException("MUI checksum mismatch: " + language);
            ValidateDataImage(path, Platform == "ARM64" ? 0xaa64 : 0x8664, true);
            var module = LoadLibraryEx(path, IntPtr.Zero, 0x22); if (module == IntPtr.Zero) throw new InvalidDataException("MUI load failed: " + language);
            try
            {
                for (var index = 0; index < NativeTexts.Length; index++)
                {
                    var expected = index == 0 ? NativeTexts[index] : texts[language][ResourceKey(NativeTexts[index])]; var text = new StringBuilder(512);
                    if (LoadString(module, (uint)(1001 + index), text, text.Capacity) == 0 || text.ToString() != expected) throw new InvalidDataException("MUI value: " + language + "/" + index);
                }
            }
            finally { FreeLibrary(module); }
        }
    }
    /// <summary>LN遵循目标架构；MUIRCT卫星为无入口无代码的PE32数据容器，不按代码DLL架构误判。</summary>
    private static void ValidateDataImage(string path, int targetMachine, bool satellite)
    {
        var bytes = File.ReadAllBytes(path); var pe = BitConverter.ToInt32(bytes, 0x3c); var optional = pe + 24;
        var machine = BitConverter.ToUInt16(bytes, pe + 4); var magic = BitConverter.ToUInt16(bytes, optional);
        if (machine != targetMachine && !(satellite && machine == 0x14c)) throw new InvalidDataException("MUI/LN architecture invalid: " + Path.GetFileName(path));
        if (magic != 0x10b && magic != 0x20b) throw new InvalidDataException("MUI/LN optional header invalid");
        var directories = optional + (magic == 0x20b ? 112 : 96);
        if (BitConverter.ToUInt32(bytes, optional + 4) != 0 || BitConverter.ToUInt32(bytes, optional + 16) != 0 || BitConverter.ToUInt32(bytes, directories + 14 * 8) != 0) throw new InvalidDataException("MUI/LN must contain no code, entry point or CLR");
    }
    /// <summary>读取标准MUI类型及共享校验和，不把普通资源DLL误认为MUI卫星。</summary>
    private static byte[] ReadMuiInfo(string path, uint expectedType)
    {
        var buffer = new byte[4096]; BitConverter.GetBytes((uint)buffer.Length).CopyTo(buffer, 0); BitConverter.GetBytes((uint)1).CopyTo(buffer, 4); uint size = (uint)buffer.Length;
        if (!GetFileMUIInfo(7, path, buffer, ref size) || BitConverter.ToUInt32(buffer, 8) != expectedType) throw new InvalidDataException("MUI metadata invalid: " + Path.GetFileName(path));
        return buffer;
    }
    /// <summary>查询资源文件的标准LN／MUI元数据，缓冲区由调用方管理。</summary>
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetFileMUIInfo(uint flags, string path, [In, Out] byte[] info, ref uint size);
    /// <summary>把原生模块作为只读资源映射，不执行模块代码。</summary>
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    /// <summary>读取指定资源字符串，返回字符数，失败为零。</summary>
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int LoadString(IntPtr module, uint id, StringBuilder text, int maximum);
    /// <summary>释放本任务映射的数据模块。</summary>
    [DllImport("kernel32")] private static extern bool FreeLibrary(IntPtr module);
}
