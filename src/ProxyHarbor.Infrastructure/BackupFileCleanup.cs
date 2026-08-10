namespace ProxyHarbor.Infrastructure;

/// <summary>Удаляет временные backup-файлы, не позволяя secondary cleanup скрыть primary failure.</summary>
internal static class BackupFileCleanup
{
    internal const string FailureDataKey = "ProxyHarbor.BackupCleanupFailure";

    /// <summary>Возвращает ожидаемую файловую ошибку вместо её выброса; отсутствие файла считается успехом.</summary>
    internal static Exception? TryDelete(string path, Action<string>? deleteFile = null)
    {
        try
        {
            if (!File.Exists(path)) return null;
            (deleteFile ?? File.Delete)(path);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception;
        }
    }

    /// <summary>
    /// Сохраняет тип и сообщение secondary failure в Data исходного исключения,
    /// после чего caller может выполнить обычный `throw;` без изменения его типа/stack.
    /// </summary>
    internal static void TryDeletePreservingPrimary(
        string path,
        Exception primaryException,
        Action<string>? deleteFile = null)
    {
        var cleanupFailure = TryDelete(path, deleteFile);
        if (cleanupFailure is null) return;
        try
        {
            var detail = $"{cleanupFailure.GetType().Name}: {cleanupFailure.Message}";
            primaryException.Data[FailureDataKey] = primaryException.Data[FailureDataKey] is string previous
                ? $"{previous} | {detail}"
                : detail;
        }
        catch (Exception)
        {
            // Даже нестандартное read-only Exception.Data не может заменить primary failure.
        }
    }
}
