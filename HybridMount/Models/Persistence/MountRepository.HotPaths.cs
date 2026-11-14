using HybridMount.Models.Mounting;

namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>只查询是否曾物化，不加载索引；已删除的物化项仍维持原有移除保护。</summary>
    internal bool HasMaterializedEntries(Guid mount)
    {
        lock (_gate)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM entries WHERE mount=$m AND materialized=1)";
            command.Parameters.AddWithValue("$m", mount.ToString("N"));
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        }
    }

    /// <summary>缓存统计只物化实际文件候选；不访问文件正文，远端删除标记的处理与原统计一致。</summary>
    internal IReadOnlyList<MountEntry> MaterializedFiles(Guid mount) =>
        SelectEntries(mount, "e.materialized=1 AND e.deleted=0 AND e.directory=0", []);

    /// <summary>保持原SourcePath字符串精确比较及首项语义；不能用NOCASE将不同S3对象键合并。</summary>
    internal MountEntry? EntryByExactSource(Guid mount, string path) =>
        SelectEntries(mount, "e.source=$p0 AND e.source COLLATE BINARY=$p0 ORDER BY e.local COLLATE HM_PATH,e.rowid LIMIT 1", [path]).FirstOrDefault();

    /// <summary>同一查询优先原身份（含墓碑），否则选择同源和本地路径的活动映射；提交前每次重新执行。</summary>
    internal MountEntry? EntryForUpload(Guid mount, string? preferredId, string source, string local) =>
        SelectEntries(mount,
            "e.source=$p0 AND e.source COLLATE BINARY=$p0 AND e.local COLLATE BINARY=$p1 " +
            "AND (e.id=$p2 OR e.deleted=0) ORDER BY CASE WHEN e.id=$p2 THEN 0 ELSE 1 END,e.rowid LIMIT 1",
            [source, local, preferredId]).FirstOrDefault();

    /// <summary>只查询当前源路径和祖先，分批保持参数上限；继续用原规则核对大小写和远端存在性。</summary>
    internal bool HasDeletedSourceAncestor(Guid mount, string path)
    {
        var ancestors = new List<string>();
        for (var current = path; ; current = RelativePaths.Parent(current))
        {
            ancestors.Add(current);
            if (current.Length == 0) break;
        }
        return ancestors.Chunk(512).Any(batch =>
            UploadRules.DeletedSourceAncestor(EntriesBySources(mount, batch), path) is not null);
    }
}
