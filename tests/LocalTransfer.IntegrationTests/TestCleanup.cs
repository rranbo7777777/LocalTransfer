namespace LocalTransfer.IntegrationTests;

/// <summary>
/// Windows 文件语义下的测试清理辅助。
/// <para>
/// <see cref="CoordinatorLog"/> 是进程级静态日志器（每次追加都是「打开-写-关闭」），xUnit
/// 默认并行运行测试类：当某个测试收尾删除临时目录时，收尾中的请求线程可能仍握着该目录下
/// <c>coordinator.log</c> 的句柄，裸 <c>Directory.Delete(recursive:true)</c> 会偶发抛
/// <see cref="IOException"/>。删除前带退避重试即可消除该竞争；重试耗尽仍失败则按原语义抛出。
/// </para>
/// </summary>
internal static class TestCleanup
{
    private const int MaxAttempts = 5;

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException
                && attempt < MaxAttempts)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
