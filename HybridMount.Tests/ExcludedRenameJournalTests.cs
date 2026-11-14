using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>改名关联和独立日志的纯回归；不注册同步根、不执行远端写入。</summary>
public sealed class ExcludedRenameJournalTests
{
    [Fact]
    public void FullScanAndOverflowKeepCopiedRenamePairs()
    {
        var queue=new LocalUploadScanQueue(8);var root=Path.Combine(Path.GetTempPath(),"rename-unit");
        queue.Record(root,new RenamedEventArgs(WatcherChangeTypes.Renamed,root,"123.bak","123.txt"),DateTimeOffset.UtcNow);
        queue.RequestFull();var batch=queue.StartFull();Assert.True(batch.IsFull);Assert.Contains(batch.Renames,x=>x.OldPath=="123.txt"&&x.NewPath=="123.bak");Assert.Empty(queue.StartFull().Renames);
        var entry=new MountEntry("entry","123.txt","123.txt","version",false);
        queue.RecordNativeRename(new("123.txt","456.bak",entry));Assert.Equal(entry,Assert.Single(queue.StartFull().Renames).Entry);
    }
    [Fact]
    public void CancelWinsOverLateSnapshotAndHistoryIsNotTheJournal()
    {
        var directory=Path.Combine(Path.GetTempPath(),"HybridMount.RenameJournal",Guid.NewGuid().ToString("N"));var id=Guid.NewGuid();
        try
        {
            var journal=new ExcludedRenameJournal(directory,id);var row=new ExcludedRenameIntent(Guid.NewGuid().ToString("N"),id,"C:\\unit-root","root",ExcludedRenameJournal.CurrentOwner,new string('A',48),"123.txt","123.bak","123.txt","entry","version",null,0,"Queued",DateTimeOffset.UtcNow,SourceScope:new string('B',64));
            journal.Add(row);journal.MarkLegacyChecked();var cancel=journal.Update(row.Id,x=>x with{CancelRequested=true});Assert.NotNull(cancel);Assert.Null(journal.Update(row.Id,x=>x with{Stage="Deleting"},row));
            var restored=new ExcludedRenameJournal(directory,id);Assert.True(restored.LegacyChecked);Assert.True(Assert.Single(restored.Read()).CancelRequested);Assert.True(cancel!.Revision>row.Revision);
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public void ReadOnlyViewsReuseContentAndOlderViewsRemainUnchanged()
    {
        var directory = Directory.CreateTempSubdirectory("HybridMount.RenameViews.").FullName;
        var mount = Guid.NewGuid();
        try
        {
            var journal = new ExcludedRenameJournal(directory, mount);
            var row = new ExcludedRenameIntent(Guid.NewGuid().ToString("N"), mount, "C:\\unit-root", "root", ExcludedRenameJournal.CurrentOwner,
                new string('A', 48), "old.txt", "new.bak", "old.txt", "entry", "v", null, 0, "Queued", DateTimeOffset.UtcNow, SourceScope: new string('B', 64));
            journal.Add(row);
            var first = journal.Read();
            Assert.Same(first, journal.Read());
            journal.MarkLegacyChecked();
            Assert.Same(first, journal.Read());
            Assert.Same(row, journal.Find(row.Id));
            Assert.Throws<NotSupportedException>(() => { ((IList<ExcludedRenameIntent>)first)[0] = row with { Stage = "Canceled" }; });
            var updated = journal.Update(row.Id, current => current with { CancelRequested = true })!;
            Assert.False(first[0].CancelRequested);
            Assert.NotSame(first, journal.Read());
            Assert.Same(updated, journal.Find(row.Id));
            var second = row with { Id = Guid.NewGuid().ToString("N"), LocalPath = "second.bak" };
            journal.Add(second);
            Assert.Single(first);
            Assert.Equal(2, journal.Read().Count);
            Assert.Same(second, journal.Find(second.Id));
            Assert.Null(journal.Find("missing"));
            var restored = new ExcludedRenameJournal(directory, mount);
            Assert.Equal(updated, restored.Find(row.Id));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void IndexedLookupStillReportsCorruptJournal()
    {
        var directory = Directory.CreateTempSubdirectory("HybridMount.RenameCorrupt.").FullName;
        var mount = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "ExcludedRenames"));
            File.WriteAllText(Path.Combine(directory, "ExcludedRenames", mount.ToString("N") + ".json"), "{broken");
            var journal = new ExcludedRenameJournal(directory, mount);
            Assert.Throws<IOException>(() => { journal.Find("missing"); });
            Assert.Throws<IOException>(() => { journal.Read(); });
        }
        finally { Directory.Delete(directory, true); }
    }
}
