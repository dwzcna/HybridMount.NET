using HybridMount.Models.UI;
using HybridMount.ViewModels;

namespace HybridMount.Tests.Unit.UI;

/// <summary>交互规则覆盖模态防重入、可用性差异、重叠业务及迟到收尾，不依赖WinUI或远端。</summary>
public sealed class InteractionStateTests
{
    [Fact]
    public void ReadOnlyTestFreezesConnectionButKeepsIndependentNewDraftEditable()
    {
        var state=new ProtocolInteractionState(Testing:true);
        Assert.False(state.CanEditConnection);Assert.True(state.CanEditDraft);Assert.True(state.CanEditSettings);
        Assert.True(state.CanChooseFolder);Assert.False(state.CanSubmit);Assert.True(state.CanTest);
        var saved=state with {Existing=true};Assert.True(saved.CanEditSettings);Assert.False(saved.CanToggleSettings);Assert.False(saved.CanChooseFolder);
    }
    [Fact]
    public void SubmitAndOtherReasonCannotUnlockEachOtherAndCancelRemainsReachable()
    {
        var state=new ProtocolInteractionState(Submitting:true,Testing:true,SavedBusy:true);
        Assert.False(state.CanSubmit);Assert.False(state.CanEditConnection);Assert.True(state.CanTest);
        state=state with {Testing=false};Assert.False(state.CanEditConnection);Assert.False(state.CanTest);
        state=state with {Submitting=false};Assert.False(state.CanEditConnection);
        state=state with {SavedBusy=false};Assert.True(state.CanEditConnection);Assert.True(state.CanSubmit);
    }
    [Fact]
    public void PublishedAvailabilityNotifiesOnlyActualDifferences()
    {
        using var vm=new ProtocolInteractionViewModel();var properties=new List<string?>();var commands=0;
        vm.PropertyChanged+=(_,e)=>properties.Add(e.PropertyName);vm.ChooseFolderCommand.CanExecuteChanged+=(_,_)=>commands++;
        var state=new ProtocolInteractionState();vm.Publish(state);Assert.True(vm.ChooseFolderCommand.CanExecute(null));
        properties.Clear();commands=0;vm.Publish(state with {});Assert.Empty(properties);Assert.Equal(0,commands);
        vm.Publish(state with {Testing=true});Assert.False(vm.CanEditConnection);Assert.True(vm.ChooseFolderCommand.CanExecute(null));
        Assert.DoesNotContain(nameof(vm.CanEditDraft),properties);Assert.DoesNotContain(nameof(vm.CanEditSettings),properties);Assert.Equal(0,commands);
        Assert.DoesNotContain(properties,name=>string.IsNullOrEmpty(name));
    }
    [Fact]
    public async Task ModalCommandRetainsEnabledAppearanceButRejectsDuplicateAndOwnsCompletion()
    {
        using var vm=new ProtocolInteractionViewModel();vm.Publish(new());
        var released=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;var errors=new List<Exception>();
        vm.SetHandler(async(action,_)=>{calls++;await released.Task;},errors.Add);
        vm.ChooseFolderCommand.Execute(null);vm.ChooseFolderCommand.Execute(null);
        Assert.Equal(1,calls);Assert.True(vm.ChooseFolderCommand.CanExecute(null));Assert.False(vm.WhenIdle.IsCompleted);
        var owned=vm.WhenIdle;released.SetResult();await owned;Assert.Empty(errors);Assert.True(vm.WhenIdle.IsCompleted);
        vm.ChooseFolderCommand.Execute(null);await vm.WhenIdle;Assert.Equal(2,calls);
    }
    [Fact]
    public async Task ViewCallbackExceptionIsObservedAndDoesNotLeakModalOwnership()
    {
        using var vm=new ProtocolInteractionViewModel();vm.Publish(new());var errors=new List<Exception>();
        vm.SetHandler((_,_)=>throw new IOException("isolated failure"),errors.Add);
        vm.ChooseFolderCommand.Execute(null);await vm.WhenIdle;Assert.Single(errors);Assert.True(vm.ChooseFolderCommand.CanExecute(null));
    }
    [Fact]
    public async Task ClosedInteractionRejectsNewWorkAndLateCompletionDoesNotReenableIt()
    {
        var vm=new ProtocolInteractionViewModel();vm.Publish(new());var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SetHandler((_,_)=>release.Task,_=>throw new Exception("late report"));vm.ChooseFolderCommand.Execute(null);var owned=vm.WhenIdle;
        vm.Dispose();vm.Publish(new());Assert.False(vm.ChooseFolderCommand.CanExecute(null));release.SetResult();await owned;Assert.False(vm.CanEditDraft);
    }
    [Theory]
    [InlineData(false,false,true)]
    [InlineData(true,false,false)]
    [InlineData(false,true,false)]
    public void CredentialsModeFreezesOnlyRelevantKeys(bool useDefault,bool testing,bool expected)
    {
        var state=new ProtocolInteractionState(UseDefaultCredentials:useDefault,Testing:testing,SeparateSqsKeys:true);
        Assert.Equal(expected,state.CanEditS3Secrets);Assert.Equal(!testing,state.CanEditSqsSecrets);
    }
    [Fact]
    public void BackupValidationAndRealWorkHaveOneSourceAndModalWaitAddsNoReason()
    {
        var vm=new BackupInteractionViewModel();vm.Publish(true,false,false);Assert.True(vm.CanSubmit);Assert.True(vm.CanEdit);
        var changes=0;vm.PropertyChanged+=(_,_)=>changes++;vm.Publish(true,false,false);Assert.Equal(0,changes);
        vm.Publish(true,true,false);Assert.False(vm.CanSubmit);Assert.False(vm.CanEdit);
        vm.Publish(true,false,true);Assert.False(vm.CanSubmit);Assert.False(vm.CanEdit);
    }
    [Fact]
    public async Task ProbeUsesCanExecuteAndKeepsCancelAvailableWhileRunning()
    {
        using var vm=new ProtocolCapabilityActionsViewModel();var released=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var starts=0;var cancels=0;
        vm.SetHandlers(async()=>{starts++;vm.Publish(true,true);await released.Task;vm.Publish(true,false);},()=>cancels++,_=>Task.CompletedTask,_=>throw new Exception());
        vm.Publish(true,false);vm.ProbeCommand.Execute(null);Assert.Equal(1,starts);Assert.True(vm.ProbeCommand.CanExecute(null));Assert.False(vm.CleanupCommand.CanExecute(Guid.Empty));
        vm.ProbeCommand.Execute(null);Assert.Equal(1,cancels);released.SetResult();await vm.ExecutionTask;Assert.True(vm.CleanupCommand.CanExecute(Guid.Empty));
    }
    [Fact]
    public async Task LegacyFolderModalKeepsAppearanceButInspectionHasIndependentOwnership()
    {
        using var vm=new RecoverySelectionViewModel();vm.UpdateSelection(1,true);var calls=0;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SetHandlers(async()=>{calls++;await release.Task;},()=>{},()=>{},_=>throw new Exception());
        vm.ChooseCommand.Execute(null);vm.ChooseCommand.Execute(null);Assert.Equal(1,calls);Assert.True(vm.CanEdit);Assert.True(vm.CanConfirm);Assert.True(vm.IsChoosing);
        vm.SetInspecting(true);Assert.False(vm.CanEdit);Assert.False(vm.CanConfirm);vm.SetInspecting(false);Assert.True(vm.CanConfirm);
        release.SetResult();await vm.WhenIdle;Assert.False(vm.IsChoosing);
    }
    [Fact]
    public async Task FinishedProbeDoesNotBlockNewWorkBehindUnrelatedHistoryReadAndEveryTaskIsOwned()
    {
        using var vm=new ProtocolCapabilityActionsViewModel();var first=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        vm.SetHandlers(async()=>{calls++;vm.Publish(true,false);if(calls==1)await first.Task;},()=>{},_=>Task.CompletedTask,_=>throw new Exception());
        vm.Publish(true,false);vm.ProbeCommand.Execute(null);vm.ProbeCommand.Execute(null);Assert.Equal(2,calls);Assert.False(vm.WhenIdle.IsCompleted);first.SetResult();await vm.WhenIdle;
    }

    [Fact]
    public void LocalConfigurationWriteDoesNotFreezeIndependentTextDraftsButKeepsMutatingCommandsProtected()
    {
        using var vm=new ProtocolInteractionViewModel();vm.Publish(new(Existing:true,SavedBusy:true));
        Assert.True(vm.CanEditSettings);Assert.True(vm.CanEditCapacityValue);Assert.False(vm.CanToggleSettings);
        Assert.False(vm.SaveCapacityCommand.CanExecute(null));Assert.False(vm.SaveIntervalsCommand.CanExecute(null));Assert.False(vm.CanSubmit);
        vm.Publish(vm.State with {SavedBusy=false});Assert.True(vm.SaveCapacityCommand.CanExecute(null));Assert.True(vm.CanToggleSettings);
    }

    [Fact]
    public void ExistingRootOptionsStayReadOnlyWithoutBlockingCredentials()
    {
        var state=new ProtocolInteractionState(Existing:true,RootEditable:false);
        Assert.True(state.CanEditConnection);Assert.False(state.CanEditRootOptions);
        Assert.True((state with {RootEditable=true}).CanEditRootOptions);
    }

    [Fact]
    public async Task DisabledCommandsRecheckBusinessStateEvenWhenExecutedDirectly()
    {
        using var probe=new ProtocolCapabilityActionsViewModel();var calls=0;
        probe.SetHandlers(()=>{calls++;return Task.CompletedTask;},()=>{},_=>{calls++;return Task.CompletedTask;},_=>throw new Exception());
        probe.ProbeCommand.Execute(null);probe.CleanupCommand.Execute(Guid.Empty);Assert.Equal(0,calls);
        probe.Publish(true,true);probe.CleanupCommand.Execute(Guid.Empty);Assert.Equal(0,calls);
        using var selection=new RecoverySelectionViewModel();selection.UpdateSelection(1,true);selection.SetHandlers(()=>Task.CompletedTask,()=>calls++,()=>calls++,_=>throw new Exception());
        selection.SetInspecting(true);selection.SelectAllCommand.Execute(null);selection.ClearAllCommand.Execute(null);Assert.Equal(0,calls);await probe.WhenIdle;
    }

}
