using HybridMount.Models.Mounting;
using HybridMount.Models.Mounting.Transactions;
namespace HybridMount.Tests;

public sealed class OperationDependencyTests
{
    [Theory]
    [InlineData("Queued")][InlineData("Conflict")][InlineData("Canceled")][InlineData("Failed")][InlineData("OutcomeUnknown")]
    public void ChildUploadWaitsForConfirmedParentAcrossRecovery(string state)
    {
        var mount=Guid.NewGuid();var parent=new LocalUpload("parent",mount,"a","a",true,"stamp",1,state,DateTimeOffset.UtcNow);
        var child=new LocalUpload("child",mount,"a/b/c.txt","a/b/c.txt",false,"stamp",1,"Queued",DateTimeOffset.UtcNow);
        Assert.False(OperationDependencies.UploadReady([parent,child],child));
        Assert.True(OperationDependencies.UploadReady([parent with{State="Completed"},child],child));
        Assert.True(OperationDependencies.UploadReady([parent],child with{SourcePath="unrelated.txt"}));
    }
    [Theory]
    [InlineData("Pending")][InlineData("Failed")][InlineData("Conflict")][InlineData("Canceled")][InlineData("Waiting")]
    public void DirectoryDeletionWaitsForEveryChildIncludingFailureAndCancellation(string state)
    {
        var mount=Guid.NewGuid();var parent=new RemotePendingChange("delete-parent",mount,"scope","a",null);
        var child=new RemotePendingChange("delete-child",mount,"scope","a/b/c.txt",null,state);
        Assert.False(OperationDependencies.RemoteReady([parent,child],parent));
        Assert.True(OperationDependencies.RemoteReady([parent,child with{State="Completed"}],parent));
        Assert.True(OperationDependencies.RemoteReady([parent,child],child));
    }
    [Fact]
    public void ParentCreationAndUnrelatedPathsHaveIndependentDependencies()
    {
        var mount=Guid.NewGuid();var parent=new RemotePendingChange("parent",mount,"scope","a",new("parent-entry","a",true,0,DateTimeOffset.UtcNow,"v1"),"Conflict");
        var child=new RemotePendingChange("child",mount,"scope","a/b.txt",new("child-entry","a/b.txt",false,1,DateTimeOffset.UtcNow,"v2"));
        Assert.False(OperationDependencies.RemoteReady([parent,child],child));
        Assert.True(OperationDependencies.RemoteReady([parent with{State="Completed"},child],child));
        Assert.True(OperationDependencies.RemoteReady([parent],child with{Path="unrelated.txt"}));
    }
}
