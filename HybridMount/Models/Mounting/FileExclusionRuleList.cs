using System.Collections;
namespace HybridMount.Models.Mounting;

/// <summary>配置内的不可变值列表；防止调用方修改数组，并保持JSON往返后的记录值相等性。</summary>
internal sealed class FileExclusionRuleList(IEnumerable<FileExclusionRule> rules) : IReadOnlyList<FileExclusionRule>,IEquatable<FileExclusionRuleList>
{
    private readonly FileExclusionRule[] _items=rules.ToArray();
    public int Count=>_items.Length;
    public FileExclusionRule this[int index]=>_items[index];
    public IEnumerator<FileExclusionRule> GetEnumerator()=>((IEnumerable<FileExclusionRule>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    public bool Equals(FileExclusionRuleList? other)=>other is not null&&_items.SequenceEqual(other._items);
    public override bool Equals(object? other)=>other is FileExclusionRuleList list&&Equals(list);
    public override int GetHashCode(){var hash=new HashCode();foreach(var rule in _items)hash.Add(rule);return hash.ToHashCode();}
}
