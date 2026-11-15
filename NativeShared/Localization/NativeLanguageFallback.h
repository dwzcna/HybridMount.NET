// 三种MUI的不可变安全回退，由相同resw键生成；构建检查阻止资源漂移。
static constexpr const wchar_t* Fallback[3][8]={
    {L"HybridMount.NET", L"释放空间", L"始终保留在此设备上", L"查看同步问题", L"在应用中定位此文件或目录的同步问题；不会自动上传。", L"管理此挂载的离线缓存；本地修改会受到保护。", L"同步问题", L"上传排除"},
    {L"HybridMount.NET", L"Free up space", L"Always keep on this device", L"View sync issues", L"Show this file or folder's sync issues in the application; does not upload automatically.", L"Manage this mount's offline cache; local edits are protected.", L"Sync issues", L"Upload exclusion"},
    {L"HybridMount.NET", L"釋放空間", L"一律保留在此裝置上", L"查看同步問題", L"在應用程式中顯示此檔案或資料夾的同步問題，不會自動上載。", L"管理此掛載的離線快取，本機修改會受到保護。", L"同步問題", L"上載排除"},
};
