namespace HybridMount.Models.Mounting;

/// <summary>业务明确选择的每挂载通道；修改事务内的核对仍属于修改通道，与 HTTP 动词无关。</summary>
internal enum CloudOperationChannel { Upload, Download, Metadata, Thumbnail }
