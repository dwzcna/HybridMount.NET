namespace HybridMount.Models.UI;

/// <summary>挂载页交给视图展示的模态入口；不含窗口或系统选择器对象。</summary>
public enum MountDialogKind { NewWebDav, NewS3, NewSynology, Import, Export, Recovery }
