using System.Runtime.CompilerServices;

// F02：Core 内部的时钟可注入重载（如 TransferItem.CreateUpload 的 clock 参数）
// 仅供单测做确定性断言；发布面保持干净（SyncCoordinator 式的公开时钟注入在此不适用）。
[assembly: InternalsVisibleTo("SshTool.Core.Tests")]
