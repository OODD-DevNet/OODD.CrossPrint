using Xunit;

// Localizer 的缓存和 CultureInfo.CurrentUICulture 都是进程级状态，
// 语言相关的用例必须串行，否则互相把 culture 改掉。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
