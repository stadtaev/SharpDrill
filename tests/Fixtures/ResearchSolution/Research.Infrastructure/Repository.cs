namespace Research.Infrastructure;
public sealed class StorageException : Exception { }
public interface IRepository { void Read(); }
public static class Repository
{
    public static void Read() { throw new StorageException(); }
    public static void Quiet() { }
    public static async Task ReadAsync() { await Task.Yield(); throw new StorageException(); }
    public static int Expression() => throw new StorageException();
    public static void StaticType(Exception error) { throw error; }
}
